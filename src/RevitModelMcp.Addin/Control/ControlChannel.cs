using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal sealed class ControlChannel
{
    private readonly string _triggerFilePath;
    private IControlSession? _session;
    private string? _lastSkippedJob;
    private readonly object _sync = new();
    private bool _executing;
    private bool _stopped;
    private ControlJobParseResult? _httpJob;
    private ControlJobParseResult? _currentJob;
    private TaskCompletionSource<string>? _httpCompletion;
    private string? _httpResponse;

    public bool TrySubmit(ControlJobParseResult job, out Task<string>? completion)
    {
        lock (_sync)
        {
            completion = null;
            if (_stopped || _executing || _session is not null || _httpJob is not null || File.Exists(_triggerFilePath))
                return false;
            _httpJob = job;
            _httpCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _httpCompletion.Task;
            return true;
        }
    }

    public ControlChannel(string triggerFilePath)
    {
        _triggerFilePath = triggerFilePath;
    }

    public bool HasActiveSession => _session is not null;

    public void Tick(UIApplication application)
    {
        lock (_sync)
        {
            if (_stopped) return;
            _executing = true;
        }
        try
        {
            if (_httpJob is null)
            {
                TickFile(application);
                return;
            }
            _currentJob = _httpJob;
            ResponseDelivery.Current = content => _httpResponse = content;
            if (_session is not null)
                ProcessActiveSession(application);
            else if (!MatchesCurrentInstance(application, _httpJob))
                TryWriteError(application, _httpJob.Command, "The target document or process does not match this endpoint.", DateTimeOffset.Now, _httpJob.CorrelationId);
            else
                ProcessJob(application, _httpJob);
        }
        catch (Exception exception)
        {
            HandleUnhandledException(application, exception);
        }
        finally
        {
            ResponseDelivery.Current = null;
            lock (_sync)
            {
                if (_httpJob is not null && _session is null)
                {
                    _httpCompletion!.TrySetResult(_httpResponse ?? CommandResponseJsonSerializer.Serialize(
                        CommandResponse<object>.Fail(_httpJob.Command, "The command ended without a response.", 0, _httpJob.CorrelationId)));
                    _httpJob = null;
                    _httpCompletion = null;
                    _httpResponse = null;
                }
                _currentJob = null;
                _executing = false;
            }
        }
    }

    private void TickFile(UIApplication application)
    {
        if (_session is not null)
        {
            ProcessActiveSession(application);
            return;
        }

        if (!File.Exists(_triggerFilePath))
        {
            _lastSkippedJob = null;
            return;
        }

        string content;
        try
        {
            content = File.ReadAllText(_triggerFilePath);
        }
        catch (IOException)
        {
            // The publisher may still be writing; the fallback timer retries the read.
            return;
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Job read failed. TriggerPath='{_triggerFilePath}'.", exception);
            if (TryDeleteTriggerFile())
            {
                TryWriteError(application, "invalid", $"Could not read the job: {exception}", DateTimeOffset.Now);
            }

            return;
        }

        var parsed = ControlJobParser.Parse(content);
        _currentJob = parsed;
        if (!MatchesCurrentInstance(application, parsed))
        {
            LogSkippedOnce(parsed, content);
            return;
        }

        _lastSkippedJob = null;
        if (!TryClaimTriggerFile(application, parsed))
        {
            return;
        }

        ProcessJob(application, parsed);
    }

    private void ProcessJob(UIApplication application, ControlJobParseResult parsed)
    {
        var startedAt = DateTimeOffset.Now;
        PluginLog.Info($"Job received. Command='{parsed.Command}'.");
        if (parsed.Cause is not null)
        {
            PluginLog.Error($"Job parsing failed. Command='{parsed.Command}'.", parsed.Cause);
        }

        var document = application.ActiveUIDocument?.Document;
        if (!ActionJobParser.IsAction(parsed.Command) && parsed.TargetDocument is not null &&
            !JobTargetMatcher.MatchesDocument(document?.Title, document?.PathName, parsed.TargetDocument))
        {
            TryWriteError(application, parsed.Command, "The active document no longer matches the target document.", startedAt, parsed.CorrelationId);
            return;
        }

        if (parsed.Kind == ControlJobKind.LegacySnapshot)
        {
            PluginLog.Info("Job processing started. Command='legacy-snapshot'.");
            try
            {
                var result = SnapshotService.CaptureAndWrite(application, startedAt);
                PluginLog.Info(
                    $"Job processing finished. Command='legacy-snapshot'. Outcome='{(result.WriteResult.Success ? "success" : "error")}'. " +
                    $"ResponsePath='{result.WriteResult.LatestPath}'. Error='{result.WriteResult.Error ?? string.Empty}'.");
            }
            catch (Exception exception)
            {
                PluginLog.Error("Legacy snapshot failed.", exception);
                TryWriteError(application, "legacy-snapshot", $"Could not execute the command: {exception}", startedAt, parsed.CorrelationId);
            }

            return;
        }

        if (ActionJobParser.IsAction(parsed.Command))
        {
            ActionCommandExecutor.Execute(application, parsed, startedAt);
            return;
        }

        if (parsed.Kind == ControlJobKind.Invalid)
        {
            PluginLog.Info($"Job processing started. Command='{parsed.Command}'.");
            TryWriteError(application, parsed.Command, parsed.Error ?? "Invalid job.", startedAt, parsed.CorrelationId);
            return;
        }

        try
        {
            PluginLog.Info($"Job processing started. Command='{parsed.Command}'.");
            if (parsed.Kind == ControlJobKind.ViewsDump)
            {
                StartSession(new ViewDumpSession(application, parsed.Views, startedAt), application);
            }
            else if (parsed.Kind == ControlJobKind.ViewElements)
            {
                StartSession(new ViewElementsSession(application, parsed, startedAt), application);
            }
            else
            {
                ReadCommandExecutor.Execute(application, parsed, startedAt);
            }
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Job start failed. Command='{parsed.Command}'.", exception);
            TryWriteError(
                application,
                parsed.Command,
                $"Could not start the command: {exception}",
                startedAt,
                parsed.CorrelationId);
            _session = null;
        }
    }

    private void RejectPendingJob(UIApplication application)
    {
        if (!File.Exists(_triggerFilePath))
        {
            return;
        }

        string parameters;
        try
        {
            parameters = File.ReadAllText(_triggerFilePath);
        }
        catch (Exception exception)
        {
            parameters = "<unreadable>";
            PluginLog.Error("Busy job could not be read before rejection.", exception);
        }

        var parsed = ControlJobParser.Parse(parameters);
        if (!MatchesCurrentInstance(application, parsed))
        {
            LogSkippedOnce(parsed, parameters);
            return;
        }

        _lastSkippedJob = null;
        if (TryClaimTriggerFile(application, parsed))
        {
            PluginLog.Warn(
                $"Job rejected while busy. Parameters='{parameters}'. TriggerPath='{_triggerFilePath}'.");
            if (parsed.CorrelationId is not null || ActionJobParser.IsAction(parsed.Command) || _httpJob is not null)
                TryWriteError(application, parsed.Command, "The add-in is busy with another command.", DateTimeOffset.Now, parsed.CorrelationId);
            else
                _session!.RejectJobWhileBusy();
        }
    }

    private void StartSession(IControlSession session, UIApplication application)
    {
        _session = session;
        ProcessActiveSession(application);
    }

    private void ProcessActiveSession(UIApplication application)
    {
        try
        {
            var delivery = ResponseDelivery.Current;
            try
            {
                ResponseDelivery.Current = null;
                RejectPendingJob(application);
            }
            finally
            {
                ResponseDelivery.Current = delivery;
            }
            _session!.ProcessTick(application);
        }
        catch (Exception exception)
        {
            // Failures outside the session handler still require a terminal response.
            PluginLog.Error("Active session failed outside its handler.", exception);
            try
            {
                _session!.Abort(exception);
            }
            catch (Exception abortException)
            {
                PluginLog.Error("Active session could not write its terminal response.", abortException);
            }
        }

        if (_session!.IsFinished)
        {
            _session = null;
        }
    }

    private bool TryDeleteTriggerFile()
    {
        try
        {
            File.Delete(_triggerFilePath);
            return true;
        }
        catch (Exception exception)
        {
            // The fallback timer retries a locked trigger.
            PluginLog.Error($"Trigger delete failed. TriggerPath='{_triggerFilePath}'.", exception);
            return false;
        }
    }

    private bool TryClaimTriggerFile(
        UIApplication application,
        ControlJobParseResult job)
    {
        try
        {
            var document = application.ActiveUIDocument?.Document;
            return JobTargetMatcher.TryClaim(
                _triggerFilePath,
                job,
                document?.Title,
                document?.PathName,
                Process.GetCurrentProcess().Id);
        }
        catch (Exception exception)
        {
            // The fallback timer retries a locked trigger.
            PluginLog.Error($"Trigger delete failed. TriggerPath='{_triggerFilePath}'.", exception);
            return false;
        }
    }

    public void Shutdown()
    {
        lock (_sync)
        {
            _stopped = true;
            _httpCompletion?.TrySetResult(CommandResponseJsonSerializer.Serialize(
                CommandResponse<object>.Fail(_httpJob?.Command ?? "invalid", "Revit is shutting down.", 0, _httpJob?.CorrelationId)));
            _httpJob = null;
            _httpCompletion = null;
        }
        if (_session is null)
        {
            return;
        }

        try
        {
            _session.Abort(new OperationCanceledException("Revit is shutting down."));
        }
        catch (Exception exception)
        {
            PluginLog.Error("Active session shutdown failed.", exception);
        }
        finally
        {
            _session = null;
        }
    }

    public void HandleUnhandledException(UIApplication application, Exception exception)
    {
        // Unhandled event failures still require a terminal response.
        PluginLog.Error("Unhandled control channel error in ExternalEvent.", exception);
        if (_session is not null)
        {
            try
            {
                _session.Abort(exception);
            }
            catch (Exception abortException)
            {
                PluginLog.Error("Active session could not write its terminal response.", abortException);
            }
            finally
            {
                _session = null;
            }

            return;
        }

        TryWriteError(
            application,
            _currentJob?.Command ?? "invalid",
            $"Job processing failed: {exception}",
            DateTimeOffset.Now,
            _currentJob?.CorrelationId);
    }

    private static void TryWriteError(
        UIApplication application,
        string command,
        string message,
        DateTimeOffset startedAt,
        string? correlationId = null)
    {
        try
        {
            ReadCommandExecutor.WriteError(application, command, message, startedAt, correlationId);
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Fallback response failed. Command='{command}'.", exception);
        }
    }

    private static bool MatchesCurrentInstance(
        UIApplication application,
        ControlJobParseResult job)
    {
        var document = application.ActiveUIDocument?.Document;
        return JobTargetMatcher.Matches(
            job,
            document?.Title,
            document?.PathName,
            Process.GetCurrentProcess().Id);
    }

    private void LogSkippedOnce(ControlJobParseResult job, string content)
    {
        var identity = $"{File.GetLastWriteTimeUtc(_triggerFilePath).Ticks}:{content}";
        if (string.Equals(_lastSkippedJob, identity, StringComparison.Ordinal))
        {
            return;
        }

        _lastSkippedJob = identity;
        PluginLog.Info(
            $"Job skipped for another Revit instance. Command='{job.Command}'. " +
            $"TargetDocument='{job.TargetDocument ?? string.Empty}'. TargetProcessId='{job.TargetProcessId?.ToString() ?? string.Empty}'.");
    }
}

internal interface IControlSession
{
    bool IsFinished { get; }

    void ProcessTick(UIApplication application);

    void RejectJobWhileBusy();

    void Abort(Exception exception);
}
