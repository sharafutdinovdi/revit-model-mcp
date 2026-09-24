using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal sealed class ControlChannel
{
    private readonly string _triggerFilePath;
    private readonly JobScheduler _scheduler = new();
    private readonly object _filesSync = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _httpCompletions = new();
    private readonly ConcurrentDictionary<string, JobCancellation> _earlyCancellations = new();
    private IControlSession? _session;
    private ScheduledJob? _current;
    private ControlJobParseResult? _currentJob;
    private DateTimeOffset _currentStartedAt;
    private long _currentQueuedMs;
    private string? _httpResponse;
    private volatile bool _stopped;

    public ControlChannel(string triggerFilePath) => _triggerFilePath = triggerFilePath;

    public JobScheduler Scheduler => _scheduler;
    public bool HasPendingWork => _session is not null || _scheduler.HasPending;

    public JobSubmission SubmitHttp(ControlJobParseResult job, string payload, out Task<string>? completion)
    {
        completion = null;
        lock (_httpCompletions)
        {
            if (_stopped) return new(null, "shutting_down", 0);
            var jobId = job.JobId ?? Guid.NewGuid().ToString("N");
            var submitted = _scheduler.Submit(jobId, job.ClientId, job.ClientName, job.Command, payload);
            if (submitted.Job is null) return submitted;
            var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _httpCompletions.Add(jobId, source);
            if (job.Kind == ControlJobKind.Jobs && job.CancelJobId is not null)
                _earlyCancellations[jobId] = CancelJob(job.CancelJobId, job.ClientId);
            completion = source.Task;
            return submitted;
        }
    }

    public void ScanPendingFiles()
    {
        lock (_filesSync)
        {
            if (_stopped) return;
            var directory = Path.GetDirectoryName(_triggerFilePath)!;
            if (!Directory.Exists(directory)) return;
            var files = Directory.GetFiles(directory, "job_*.json").OrderBy(File.GetCreationTimeUtc).ToList();
            if (File.Exists(_triggerFilePath)) files.Add(_triggerFilePath);
            foreach (var path in files)
            {
                string content;
                try { content = File.ReadAllText(path); }
                catch (IOException) { continue; }
                var parsed = ControlJobParser.Parse(content);
                if (parsed.TargetProcessId is int target && target != Process.GetCurrentProcess().Id) continue;
                var jobId = parsed.JobId ?? Guid.NewGuid().ToString("N");
                var submitted = _scheduler.Submit(jobId, parsed.ClientId, parsed.ClientName, parsed.Command, content);
                if (submitted.Job is null)
                {
                    if (submitted.Error == "queue_full")
                    {
                        var response = CommandResponse<object>.Fail(parsed.Command, "queue_full", 0, parsed.CorrelationId);
                        response.Error = "queue_full";
                        response.RetryAfterMs = submitted.RetryAfterMs;
                        response.Client = new ClientIdentity { Name = parsed.ClientName, Id = parsed.ClientId };
                        response.JobId = jobId;
                        var responsePath = CommandResponseJsonFile.CreatePath(SnapshotFileWriter.OutputDirectory,
                            DateTime.Now, parsed.Command, parsed.CorrelationId);
                        CommandResponseJsonFile.Write(responsePath, response);
                    }
                    PluginLog.Warn($"File job rejected. Error='{submitted.Error}'.");
                }
                else if (parsed.Kind == ControlJobKind.Jobs && parsed.CancelJobId is not null)
                    _earlyCancellations[jobId] = CancelJob(parsed.CancelJobId, parsed.ClientId);
                try { File.Delete(path); }
                catch (IOException) { PluginLog.Warn("Claimed job file could not be removed."); }
            }
        }
    }

    public void Tick(UIApplication application)
    {
        try
        {
            if (_stopped) return;
            ScanPendingFiles();
            if (_session is null)
            {
                _current = _scheduler.TakeNext();
                if (_current is null) return;
                _currentJob = ControlJobParser.Parse(_current.Payload);
                _currentStartedAt = DateTimeOffset.Now;
                _currentQueuedMs = Math.Max(0,
                    (long)(DateTimeOffset.UtcNow - _current.SubmittedUtc).TotalMilliseconds);
            }
            if (_current is null || _currentJob is null) return;
            JobResponseMetadata.Current = new JobResponseMetadata(
                new ClientIdentity { Name = _current.ClientName, Id = _current.ClientId },
                _current.JobId,
                _currentQueuedMs);
            var isHttp = HasHttpCompletion(_current.JobId);
            if (isHttp) ResponseDelivery.Current = content => _httpResponse = content;
            if (_session is not null)
            {
                if (_scheduler.IsCancellationRequested(_current.JobId))
                {
                    _session.Abort(new OperationCanceledException("Read job cancelled by its client."));
                    _session = null;
                }
                else ProcessActiveSession(application);
            }
            else if (!MatchesCurrentInstance(application, _currentJob))
                TryWriteError(application, _currentJob.Command,
                    "The target document or process does not match this endpoint.", _currentStartedAt,
                    _currentJob.CorrelationId);
            else ProcessJob(application, _currentJob);
        }
        catch (Exception exception)
        {
            HandleUnhandledException(application, exception);
        }
        finally
        {
            ResponseDelivery.Current = null;
            JobResponseMetadata.Current = null;
            if (_current is not null && _session is null)
            {
                var fallback = CommandResponse<object>.Fail(_current.Command,
                    "The command ended without a response.", 0, _currentJob?.CorrelationId);
                fallback.Client = new ClientIdentity { Name = _current.ClientName, Id = _current.ClientId };
                fallback.JobId = _current.JobId;
                fallback.QueuedMs = _currentQueuedMs;
                var response = _httpResponse ?? ReadFileResult() ?? CommandResponseJsonSerializer.Serialize(fallback);
                _scheduler.Complete(_current.JobId, response, IsSuccessfulOrPartial(response));
                lock (_httpCompletions)
                {
                    if (_httpCompletions.Remove(_current.JobId, out var completion)) completion.TrySetResult(response);
                }
                _current = null;
                _currentJob = null;
                _httpResponse = null;
            }
        }
    }

    private bool HasHttpCompletion(string jobId)
    {
        lock (_httpCompletions) return _httpCompletions.ContainsKey(jobId);
    }

    private string? ReadFileResult()
    {
        if (_currentJob?.CorrelationId is null) return null;
        var path = CommandResponseJsonFile.CreatePath(SnapshotFileWriter.OutputDirectory,
            _currentStartedAt.LocalDateTime, _currentJob.Command, _currentJob.CorrelationId);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
    }

    private static bool IsSuccessfulOrPartial(string json)
    {
        try
        {
            using var reader = JsonReaderWriterFactory.CreateJsonReader(
                Encoding.UTF8.GetBytes(json), System.Xml.XmlDictionaryReaderQuotas.Max);
            var response = XElement.Load(reader);
            return response.Element("success")?.Value == "true" || response.Element("partial")?.Value == "true";
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or System.Runtime.Serialization.SerializationException)
        {
            return false;
        }
    }

    public JobCancellation CancelJob(string jobId, string clientId)
    {
        var job = _scheduler.Status(jobId);
        var cancellation = _scheduler.Cancel(jobId, clientId,
            job is not null && ActionJobParser.IsAction(job.Command));
        if (!cancellation.Cancelled || cancellation.State != JobState.Cancelled || job is null)
            return cancellation;
        var parsed = ControlJobParser.Parse(job.Payload);
        var response = CommandResponse<object>.Fail(job.Command, "Job cancelled by its client.", 0, parsed.CorrelationId);
        response.Client = new ClientIdentity { Name = job.ClientName, Id = job.ClientId };
        response.JobId = job.JobId;
        response.QueuedMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - job.SubmittedUtc).TotalMilliseconds);
        var json = CommandResponseJsonSerializer.Serialize(response);
        _scheduler.SetCancelledResult(jobId, json);
        lock (_httpCompletions)
        {
            if (_httpCompletions.Remove(jobId, out var completion))
            {
                completion.TrySetResult(json);
                return cancellation;
            }
        }
        var path = CommandResponseJsonFile.CreatePath(SnapshotFileWriter.OutputDirectory,
            DateTime.Now, job.Command, parsed.CorrelationId);
        CommandResponseJsonFile.Write(path, response);
        return cancellation;
    }

    private void ProcessJob(UIApplication application, ControlJobParseResult parsed)
    {
        var startedAt = _currentStartedAt;
        PluginLog.Info($"Job received. Command='{parsed.Command}'. Client='{parsed.ClientName}'. JobId='{_current?.JobId}'.");
        var document = application.ActiveUIDocument?.Document;
        if (!ActionJobParser.IsAction(parsed.Command) && parsed.TargetDocument is not null &&
            !JobTargetMatcher.MatchesDocument(document?.Title, document?.PathName, parsed.TargetDocument))
        {
            TryWriteError(application, parsed.Command, "The active document no longer matches the target document.", startedAt, parsed.CorrelationId);
            return;
        }
        if (parsed.Kind == ControlJobKind.LegacySnapshot)
        {
            SnapshotService.CaptureAndWrite(application, startedAt);
            return;
        }
        if (parsed.Kind == ControlJobKind.Jobs)
        {
            var cancellation = parsed.CancelJobId is null ? null :
                _earlyCancellations.TryRemove(_current!.JobId, out var early) ? early :
                CancelJob(parsed.CancelJobId, parsed.ClientId);
            var data = new JobListData
            {
                Jobs = _scheduler.ActiveJobs().Select(job => new JobSummary
                {
                    JobId = job.JobId, ClientName = job.ClientName, Command = job.Command,
                    State = StateName(job.State), Position = job.Position,
                    AgeMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - job.SubmittedUtc).TotalMilliseconds)
                }).ToList(),
                Cancellation = cancellation is null ? null : new JobCancellationInfo
                {
                    Cancelled = cancellation.Cancelled,
                    State = cancellation.State is null ? null : StateName(cancellation.State.Value),
                    Message = cancellation.Message
                }
            };
            var response = CommandResponse<JobListData>.Ok("jobs", data, 0);
            response.CorrelationId = parsed.CorrelationId;
            var json = CommandResponseJsonSerializer.Serialize(response);
            if (ResponseDelivery.Current is { } delivery) delivery(json);
            else WriteFileResponse(parsed, json, startedAt);
            return;
        }
        if (ActionJobParser.IsAction(parsed.Command))
        {
            ActionCommandExecutor.Execute(application, parsed, startedAt);
            return;
        }
        if (parsed.Kind == ControlJobKind.Invalid)
        {
            TryWriteError(application, parsed.Command, parsed.Error ?? "Invalid job.", startedAt, parsed.CorrelationId);
            return;
        }
        if (parsed.Kind == ControlJobKind.ViewsDump)
            StartSession(new ViewDumpSession(application, parsed.Views, startedAt), application);
        else if (parsed.Kind == ControlJobKind.ViewElements)
            StartSession(new ViewElementsSession(application, parsed, startedAt), application);
        else ReadCommandExecutor.Execute(application, parsed, startedAt);
    }

    private static void WriteFileResponse(ControlJobParseResult job, string json, DateTimeOffset startedAt)
    {
        var path = CommandResponseJsonFile.CreatePath(SnapshotFileWriter.OutputDirectory,
            startedAt.LocalDateTime, job.Command, job.CorrelationId);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private void StartSession(IControlSession session, UIApplication application)
    {
        _session = session;
        _scheduler.MarkCancellable(_current!.JobId);
        ProcessActiveSession(application);
    }

    private void ProcessActiveSession(UIApplication application)
    {
        try { _session!.ProcessTick(application); }
        catch (Exception exception)
        {
            PluginLog.Error("Active session failed outside its handler.", exception);
            _session!.Abort(exception);
        }
        if (_session!.IsFinished) _session = null;
    }

    public void Shutdown()
    {
        _stopped = true;
        lock (_httpCompletions)
        {
            foreach (var completion in _httpCompletions.Values)
                completion.TrySetResult("{\"success\":false,\"error\":\"Revit is shutting down.\"}");
            _httpCompletions.Clear();
        }
        if (_session is not null)
        {
            try { _session.Abort(new OperationCanceledException("Revit is shutting down.")); }
            catch (Exception exception) { PluginLog.Error("Active session shutdown failed.", exception); }
            _session = null;
        }
    }

    public void HandleUnhandledException(UIApplication application, Exception exception)
    {
        PluginLog.Error("Unhandled control channel error in ExternalEvent.", exception);
        if (_session is not null)
        {
            try { _session.Abort(exception); }
            catch (Exception abortException) { PluginLog.Error("Active session could not write its terminal response.", abortException); }
            _session = null;
            return;
        }
        TryWriteError(application, _currentJob?.Command ?? "invalid", $"Job processing failed: {exception}",
            DateTimeOffset.Now, _currentJob?.CorrelationId);
    }

    private static void TryWriteError(UIApplication application, string command, string message,
        DateTimeOffset startedAt, string? correlationId = null)
    {
        try { ReadCommandExecutor.WriteError(application, command, message, startedAt, correlationId); }
        catch (Exception exception) { PluginLog.Error($"Fallback response failed. Command='{command}'.", exception); }
    }

    private static bool MatchesCurrentInstance(UIApplication application, ControlJobParseResult job)
    {
        var document = application.ActiveUIDocument?.Document;
        return JobTargetMatcher.Matches(job, document?.Title, document?.PathName, Process.GetCurrentProcess().Id);
    }

    internal static string StateName(JobState state) => state switch
    {
        JobState.WaitingRevit => "waiting_revit",
        _ => state.ToString().ToLowerInvariant()
    };
}

internal interface IControlSession
{
    bool IsFinished { get; }
    void ProcessTick(UIApplication application);
    void RejectJobWhileBusy();
    void Abort(Exception exception);
}
