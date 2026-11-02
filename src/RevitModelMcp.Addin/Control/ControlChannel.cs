using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Autodesk.Revit.UI;
using RevitModelMcp.Activity;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Batch;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;
using RevitModelMcp.Output;
using RevitModelMcp.Policy;

namespace RevitModelMcp.Control;

internal sealed class ControlChannel
{
    private readonly string _channelDirectory;
    private readonly JobScheduler _scheduler;
    private readonly TabActivityVisuals _tabActivity = new();
    private readonly object _filesSync = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _httpCompletions = new();
    private IControlSession? _session;
    private ScheduledJob? _current;
    private ControlJobParseResult? _currentJob;
    private DateTimeOffset _currentStartedAt;
    private long _currentQueuedMs;
    private string? _httpResponse;
    private DateTimeOffset? _responseDeliveredAt;
    private volatile bool _stopped;

    public ControlChannel(string channelDirectory)
    {
        _channelDirectory = channelDirectory;
        _scheduler = new JobScheduler(resultDirectory: Path.Combine(channelDirectory, "jobs"), log: PluginLog.Warn);
    }

    public void ConfigureTabActivity(bool enabled) => _tabActivity.Configure(enabled);

    public JobScheduler Scheduler => _scheduler;
    public bool HasPendingWork => _session is not null || _scheduler.HasPending;
    public bool FileChannelEnabled { get; set; }

    public JobSubmission SubmitHttp(ControlJobParseResult job, string payload, out Task<string>? completion)
    {
        completion = null;
        lock (_httpCompletions)
        {
            if (_stopped) return new(null, "shutting_down", 0);
            var jobId = string.IsNullOrWhiteSpace(job.JobId) ? Guid.NewGuid().ToString("N") : job.JobId!;
            var submitted = _scheduler.Submit(jobId, job.ClientId, job.ClientName, job.Command, payload);
            if (submitted.Job is null) return submitted;
            var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _httpCompletions.Add(jobId, source);
            completion = source.Task;
            return submitted;
        }
    }

    public void ScanPendingFiles(UIApplication? application = null)
    {
        lock (_filesSync)
        {
            if (_stopped || !FileChannelEnabled) return;
            var directory = _channelDirectory;
            if (!Directory.Exists(directory)) return;
            var files = Directory.GetFiles(directory, "job_*.json").OrderBy(File.GetCreationTimeUtc).ToList();
            foreach (var path in files)
            {
                string content;
                try
                {
                    if (new FileInfo(path).Length > PipeProtocol.MaxMessageBytes)
                    {
                        File.Delete(path);
                        PluginLog.Warn($"File job skipped: over 1 MiB. Name='{Path.GetFileName(path)}'.");
                        continue;
                    }
                    content = File.ReadAllText(path);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                var parsed = ControlJobParser.Parse(content);
                if (!MatchesFileTarget(parsed, application)) continue;
                var claimedPath = $"{path}.{Guid.NewGuid():N}.claimed";
                try { File.Move(path, claimedPath); }
                catch (IOException) { continue; }
                try
                {
                    if (new FileInfo(claimedPath).Length > PipeProtocol.MaxMessageBytes)
                    {
                        File.Delete(claimedPath);
                        PluginLog.Warn($"File job skipped: over 1 MiB. Name='{Path.GetFileName(path)}'.");
                        continue;
                    }
                    content = File.ReadAllText(claimedPath);
                }
                catch (IOException)
                {
                    File.Move(claimedPath, Path.Combine(directory, $"job_{Guid.NewGuid():N}.json"));
                    continue;
                }
                parsed = ControlJobParser.Parse(content);
                if (!MatchesFileTarget(parsed, application))
                {
                    File.Move(claimedPath, Path.Combine(directory, $"job_{Guid.NewGuid():N}.json"));
                    continue;
                }
                var jobId = string.IsNullOrWhiteSpace(parsed.JobId) ? Guid.NewGuid().ToString("N") : parsed.JobId!;
                var submitted = _scheduler.Submit(jobId, parsed.ClientId, parsed.ClientName, parsed.Command, content);
                if (submitted.Job is null)
                {
                    if (submitted.Error is "queue_full" or "invalid_job_id" or "persist_failed")
                    {
                        var message = JobScheduler.SubmissionMessage(submitted.Error);
                        var response = CommandResponse<object>.Fail(parsed.Command, message ?? submitted.Error, 0, parsed.CorrelationId);
                        response.Error = submitted.Error;
                        response.RetryAfterMs = submitted.RetryAfterMs;
                        response.Client = new ClientIdentity { Name = parsed.ClientName, Id = parsed.ClientId };
                        response.JobId = jobId;
                        response.Responder.ProcessId = Process.GetCurrentProcess().Id;
                        var responsePath = CommandResponseJsonFile.CreatePath(ChannelDirectory.OutputDirectory,
                            DateTime.Now, parsed.Command, parsed.CorrelationId);
                        CommandResponseJsonFile.Write(responsePath, response);
                    }
                    PluginLog.Warn($"File job rejected. Error='{submitted.Error}'.");
                }
                try { File.Delete(claimedPath); }
                catch (IOException) { PluginLog.Warn("Claimed job file could not be removed."); }
            }
        }
    }

    private static bool MatchesFileTarget(ControlJobParseResult job, UIApplication? application)
    {
        if (job.TargetProcessId is int target) return target == Process.GetCurrentProcess().Id;
        if (job.TargetDocument is null) return true;
        if (application is null) return false;
        var document = application.ActiveUIDocument?.Document;
        return JobTargetMatcher.MatchesDocument(document?.Title, document?.PathName, job.TargetDocument);
    }

    public void Tick(UIApplication application)
    {
        try
        {
            if (_stopped) return;
            ScanPendingFiles(application);
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
            ResponseDelivery.Current = content =>
            {
                _httpResponse = content;
                _responseDeliveredAt = DateTimeOffset.UtcNow;
                _tabActivity.Progress(_current.JobId, content);
                if (ActionJobParser.IsAction(_current.Command)) _scheduler.PublishProgress(_current.JobId, content);
                if (!isHttp)
                {
                    var path = CommandResponseJsonFile.CreatePath(ChannelDirectory.OutputDirectory,
                        _currentStartedAt.LocalDateTime, _currentJob.Command, _currentJob.CorrelationId);
                    CommandResponseJsonFile.WriteContent(path, content);
                }
            };
            ResponseDelivery.CancellationRequested = () =>
            {
                if (File.Exists(Path.Combine(_channelDirectory, "jobs", $"{_current.JobId}.cancel")))
                    _scheduler.Cancel(_current.JobId, _current.ClientId, true);
                return _scheduler.IsCancellationRequested(_current.JobId);
            };
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
            ResponseDelivery.CancellationRequested = null;
            JobResponseMetadata.Current = null;
            if (_current is not null && _session is null)
            {
                var fallback = CommandResponse<object>.Fail(_current.Command,
                    "The command ended without a response.", 0, _currentJob?.CorrelationId);
                fallback.Error = fallback.Message;
                fallback.Client = new ClientIdentity { Name = _current.ClientName, Id = _current.ClientId };
                fallback.JobId = _current.JobId;
                fallback.QueuedMs = _currentQueuedMs;
                fallback.Responder.ProcessId = Process.GetCurrentProcess().Id;
                var response = _httpResponse ?? ReadFileResult() ?? CommandResponseJsonSerializer.Serialize(fallback);
                _scheduler.Complete(_current.JobId, response, IsSuccessfulOrPartial(response));
                _tabActivity.End(_current.JobId, HasHttpCompletion(_current.JobId)
                    ? DateTimeOffset.UtcNow : _responseDeliveredAt ?? DateTimeOffset.UtcNow);
                lock (_httpCompletions)
                {
                    if (_httpCompletions.Remove(_current.JobId, out var completion)) completion.TrySetResult(response);
                }
                _current = null;
                _currentJob = null;
                _httpResponse = null;
                _responseDeliveredAt = null;
            }
        }
    }

    private void StartTabActivity(UIApplication application, ScheduledJob job, ControlJobParseResult parsed)
    {
        try
        {
            var document = parsed.TargetDocument is null
                ? application.ActiveUIDocument?.Document
                : application.Application.Documents.Cast<Autodesk.Revit.DB.Document>().FirstOrDefault(candidate =>
                    JobTargetMatcher.MatchesDocument(candidate.Title, candidate.PathName, parsed.TargetDocument));
            if (document is null) return;
            var pointer = IntPtr.Zero;
            try
            {
                var nativeDocument = document.GetType().GetMethod("getMFCDoc", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(document, null);
                var value = nativeDocument?.GetType().GetMethod("GetPointerValue", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(nativeDocument, null);
                if (value is IntPtr address) pointer = address;
            }
            catch (Exception exception) { PluginLog.Debug("Document tab native identity is unavailable.", exception); }
            _tabActivity.Start(job, document, document.Title, pointer, parsed.Action?.Detail);
        }
        catch (Exception exception) { _tabActivity.ReportFailure(exception); }
    }

    private bool HasHttpCompletion(string jobId)
    {
        lock (_httpCompletions) return _httpCompletions.ContainsKey(jobId);
    }

    private string? ReadFileResult()
    {
        if (_currentJob?.CorrelationId is null) return null;
        var path = CommandResponseJsonFile.CreatePath(ChannelDirectory.OutputDirectory,
            _currentStartedAt.LocalDateTime, _currentJob.Command, _currentJob.CorrelationId);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsSuccessfulOrPartial(string json) =>
        ResultEnvelope.TryReadFlags(json, out var success, out var partial) && (success || partial);

    public JobCancellation CancelJob(string jobId, string clientId) => CancelJob(jobId, clientId, true);

    private JobCancellation CancelJob(string jobId, string clientId, bool isAction)
    {
        var job = _scheduler.Status(jobId);
        if (job is not null && ActionJobParser.IsAction(job.Command) && ActionCommandExecutor.ReadOnlyMode)
            return new(false, job.State, PolicyProvider.ReadOnlyMessage);
        var cancellation = _scheduler.Cancel(jobId, clientId,
            isAction && job is not null && ActionJobParser.IsAction(job.Command));
        if (!cancellation.Cancelled || cancellation.State != JobState.Cancelled || job is null)
            return cancellation;
        var parsed = ControlJobParser.Parse(job.Payload);
        var response = CommandResponse<ActionResultData>.Fail(job.Command, "Job cancelled by its client.", 0, parsed.CorrelationId);
        if (job.Command == "process-models") response.Data = new ActionResultData { Cancelled = true, Models = [] };
        response.Client = new ClientIdentity { Name = job.ClientName, Id = job.ClientId };
        response.JobId = job.JobId;
        response.QueuedMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - job.SubmittedUtc).TotalMilliseconds);
        response.Responder.ProcessId = Process.GetCurrentProcess().Id;
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
        var path = CommandResponseJsonFile.CreatePath(ChannelDirectory.OutputDirectory,
            DateTime.Now, job.Command, parsed.CorrelationId);
        CommandResponseJsonFile.Write(path, response);
        return cancellation;
    }

    private void ProcessJob(UIApplication application, ControlJobParseResult parsed)
    {
        var startedAt = _currentStartedAt;
        if (Environment.GetEnvironmentVariable("REVIT_MCP_BATCH_WORKER") == "1" &&
            !BatchReadOnlyPolicy.Allows(parsed.Command))
        {
            TryWriteError(application, parsed.Command, "read-only batch mode", startedAt, parsed.CorrelationId);
            return;
        }
        PluginLog.Info($"Job received. Command='{parsed.Command}'. Client='{parsed.ClientName}'. JobId='{_current?.JobId}'.");
        if (PolicyProvider.Refusal(parsed.Command) is { } policyRefusal)
        {
            TryWriteError(application, parsed.Command, policyRefusal, startedAt, parsed.CorrelationId);
            return;
        }
        var document = application.ActiveUIDocument?.Document;
        if (!ActionJobParser.IsAction(parsed.Command) && parsed.TargetDocument is not null &&
            !JobTargetMatcher.MatchesDocument(document?.Title, document?.PathName, parsed.TargetDocument))
        {
            if (!application.Application.Documents.Cast<Autodesk.Revit.DB.Document>().Any(candidate =>
                    JobTargetMatcher.MatchesDocument(candidate.Title, candidate.PathName, parsed.TargetDocument)))
            {
                TryWriteError(application, parsed.Command,
                    $"Target document '{parsed.TargetDocument}' is not open; the active document is '{document?.Title ?? "<none>"}'. Pass `document` or call revit_activate_document.",
                    startedAt, parsed.CorrelationId);
                return;
            }
        }
        if (parsed.Kind == ControlJobKind.Jobs)
        {
            var data = new JobListData
            {
                Jobs = _scheduler.ActiveJobs().Select(job => new JobSummary
                {
                    JobId = job.JobId,
                    ClientName = job.ClientName,
                    Command = job.Command,
                    State = StateName(job.State),
                    Position = job.Position,
                    AgeMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - job.SubmittedUtc).TotalMilliseconds)
                }).ToList()
            };
            var response = CommandResponse<JobListData>.Ok("jobs", data, 0);
            CommandResponseFileWriter.Create(startedAt.LocalDateTime, parsed.Command,
                ReadCommandReader.ReadResponder(application), parsed.CorrelationId).Write(response);
            return;
        }
        if (_current is not null && parsed.Kind != ControlJobKind.Invalid) StartTabActivity(application, _current, parsed);
        if (ActionJobParser.IsAction(parsed.Command))
        {
            if (parsed.Command == "process-models") _scheduler.MarkCancellable(_current!.JobId);
            ActionCommandExecutor.Execute(application, parsed, startedAt);
            return;
        }
        if (parsed.Kind is ControlJobKind.BatchSupervisorStart or ControlJobKind.BatchPrePass or
            ControlJobKind.BatchOpen or ControlJobKind.BatchSnapshot or ControlJobKind.BatchClose)
        {
            BatchCommands.Execute(application, parsed, startedAt);
            return;
        }
        if (parsed.Kind == ControlJobKind.Invalid)
        {
            TryWriteError(application, parsed.Command, parsed.Error ?? "Invalid job.", startedAt, parsed.CorrelationId);
            return;
        }
        if (parsed.Kind == ControlJobKind.ViewElements)
            StartSession(new ViewElementsSession(application, parsed, startedAt), application);
        else ReadCommandExecutor.Execute(application, parsed, startedAt);
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
        _tabActivity.Stop();
        lock (_httpCompletions)
        {
            var abandoned = _scheduler.AbandonUnfinished(ShutdownResults.Running, ShutdownResults.Queued);
            foreach (var completion in _httpCompletions)
            {
                var job = abandoned.FirstOrDefault(item => item.JobId == completion.Key);
                completion.Value.TrySetResult(job?.Result ?? ShutdownResults.Unknown);
            }
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
        TryWriteError(application, _currentJob?.Command ?? "invalid",
            $"Job processing failed ({exception.GetType().Name}).",
            _currentStartedAt, _currentJob?.CorrelationId);
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
        JobState.Abandoned => "abandoned",
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

internal sealed class TabActivityVisuals
{
    private readonly TabActivityTracker _tracker = new(() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, TabDocument> _documents = new();
    private readonly Dictionary<TabItem, SavedTab> _tabs = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _layoutTimer;
    private FrameworkElement? _root;
    private int _failures;
    private bool _disabled;
    private bool _queued;
    private bool _refreshQueued;
    private bool _stopped;

    public void Configure(bool enabled)
    {
        _tracker.Configure(enabled);
        Dispatch(Refresh);
    }

    public void Start(ScheduledJob job, object document, string title, IntPtr pointer, string? detail)
    {
        try
        {
            if (_disabled || _stopped) return;
            _dispatcher.VerifyAccess();
            var existing = _documents.FirstOrDefault(pair => ReferenceEquals(pair.Value.Reference, document));
            var documentId = existing.Key ?? Guid.NewGuid().ToString("N");
            _documents[documentId] = new TabDocument(document, title, pointer);
            _tracker.Start(job.JobId, documentId, job.ClientName, job.Command,
                ActionJobParser.IsAction(job.Command) ? ActivityTitleBuilder.BuildRunning(job.Command) : null, detail);
            Refresh();
            if (_tabs.Count > 0) _dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            _failures = 0;
        }
        catch (Exception exception) { Failed(exception); }
    }

    public void Progress(string jobId, string response)
    {
        try
        {
            using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(response),
                System.Xml.XmlDictionaryReaderQuotas.Max);
            var envelope = XElement.Load(reader);
            var data = envelope.Element("data");
            int? Counter(string name) => int.TryParse(data?.Element(name)?.Value, out var value) ? value : null;
            _tracker.Progress(jobId, Counter("currentIndex") ?? Counter("processed"), Counter("total"),
                data?.Element("summary") is { HasElements: false } summary ? summary.Value : null);
            if (!_refreshQueued)
            {
                _refreshQueued = true;
                Dispatch(() => { _refreshQueued = false; RefreshToolTips(); });
            }
        }
        catch (Exception exception) { ReportFailure(exception); }
    }

    public void End(string jobId, DateTimeOffset responseDeliveredAt)
    {
        try
        {
            _dispatcher.VerifyAccess();
            _tracker.End(jobId, responseDeliveredAt);
            ScheduleTimer();
        }
        catch (Exception exception) { ReportFailure(exception); }
    }

    public void Stop() => Dispatch(() =>
    {
        _stopped = true;
        _tracker.Configure(false);
        _timer?.Stop();
        _layoutTimer?.Stop();
        if (_root is not null) _root.LayoutUpdated -= OnLayoutUpdated;
        RestoreAll();
        _documents.Clear();
    });

    public void ReportFailure(Exception exception) => Dispatch(() => Failed(exception), resetFailures: false);

    private void Failed(Exception exception)
    {
        PluginLog.Debug("Document tab activity could not be applied.", exception);
        if (++_failures < 3) return;
        _disabled = true;
        try
        {
            _timer?.Stop();
            _layoutTimer?.Stop();
            if (_root is not null) _root.LayoutUpdated -= OnLayoutUpdated;
            RestoreAll();
            _documents.Clear();
        }
        catch (Exception restoreException) { PluginLog.Debug("Document tab activity restore failed.", restoreException); }
    }

    private void Dispatch(Action action, bool resetFailures = true)
    {
        if (_disabled || _stopped) return;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    if (_disabled || _stopped) return;
                    action();
                    if (resetFailures) _failures = 0;
                }
                catch (Exception exception) { Failed(exception); }
            }));
        }
        catch (Exception exception) { PluginLog.Debug("Document tab dispatcher is unavailable.", exception); }
    }

    private void Refresh()
    {
        _timer?.Stop();
        var active = _tracker.Snapshot().ToDictionary(state => state.DocumentId);
        if (active.Count == 0)
        {
            RestoreAll();
            _documents.Clear();
            _layoutTimer?.Stop();
            if (_root is not null) _root.LayoutUpdated -= OnLayoutUpdated;
            _root = null;
            _queued = false;
            return;
        }
        if (_root is null)
        {
            _root = HwndSource.FromHwnd(Autodesk.Windows.ComponentManager.ApplicationWindow)?.RootVisual as FrameworkElement
                ?? System.Windows.Application.Current?.MainWindow;
            if (_root is not null)
            {
                _layoutTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _layoutTimer.Tick += OnLayoutTimer;
                _root.LayoutUpdated += OnLayoutUpdated;
            }
        }
        if (_root is not null)
        {
            var present = new HashSet<TabItem>();
            foreach (var tab in Descendants(_root).OfType<TabItem>().Where(IsDocumentTab))
            {
                present.Add(tab);
                var documentId = Match(tab);
                if (_tabs.TryGetValue(tab, out var saved) && saved.DocumentId != documentId)
                {
                    Restore(tab, saved);
                    _tabs.Remove(tab);
                }
                if (documentId is null || !active.TryGetValue(documentId, out var state)) continue;
                if (!_tabs.TryGetValue(tab, out saved))
                {
                    saved = new SavedTab(documentId, tab);
                    _tabs.Add(tab, saved);
                    _tracker.MarkVisible(documentId);
                    var color = PaneTheme.IsDark ? System.Windows.Media.Color.FromRgb(0x1F, 0x4E, 0x79) : System.Windows.Media.Color.FromRgb(0x2F, 0x6D, 0xB5);
                    saved.Brush = new SolidColorBrush(color);
                    saved.Brush.Freeze();
                    saved.TextBrush = new SolidColorBrush(Colors.White);
                    saved.TextBrush.Freeze();
                    tab.SetCurrentValue(System.Windows.Controls.Control.BackgroundProperty, saved.Brush);
                    tab.SetCurrentValue(System.Windows.Controls.Control.ForegroundProperty, saved.TextBrush);
                }
                if (saved.ToolTip is null || ReferenceEquals(tab.ToolTip, saved.ToolTip))
                {
                    if (!ReferenceEquals(tab.ToolTip, saved.ToolTip) || !Equals(tab.ToolTip, state.ToolTip))
                    {
                        saved.ToolTip = state.ToolTip;
                        tab.SetCurrentValue(FrameworkElement.ToolTipProperty, saved.ToolTip);
                    }
                }
            }
            foreach (var pair in _tabs.ToArray())
                if (!present.Contains(pair.Key) || !active.ContainsKey(pair.Value.DocumentId))
                {
                    Restore(pair.Key, pair.Value);
                    _tabs.Remove(pair.Key);
                }
        }
        ScheduleTimer();
    }

    private void RefreshToolTips()
    {
        var active = _tracker.Snapshot().ToDictionary(state => state.DocumentId);
        foreach (var pair in _tabs)
        {
            if (!active.TryGetValue(pair.Value.DocumentId, out var state)) continue;
            var saved = pair.Value;
            if (saved.ToolTip is not null && !ReferenceEquals(pair.Key.ToolTip, saved.ToolTip)) continue;
            if (Equals(pair.Key.ToolTip, state.ToolTip)) continue;
            saved.ToolTip = state.ToolTip;
            pair.Key.SetCurrentValue(FrameworkElement.ToolTipProperty, saved.ToolTip);
        }
    }

    private void ScheduleTimer()
    {
        _timer?.Stop();
        if (_tracker.NextChange is not { } delay) return;
        _timer ??= new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _timer.Tick -= OnTimer;
        _timer.Tick += OnTimer;
        _timer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs args)
    {
        try
        {
            _timer?.Stop();
            if (_disabled || _stopped) return;
            Refresh();
            _failures = 0;
        }
        catch (Exception exception) { Failed(exception); }
    }

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        try
        {
            if (_queued || _disabled || _stopped) return;
            _queued = true;
            _layoutTimer?.Start();
        }
        catch (Exception exception)
        {
            PluginLog.Debug("Document tab layout scheduling failed.", exception);
            ReportFailure(exception);
        }
    }

    private void OnLayoutTimer(object? sender, EventArgs args)
    {
        try
        {
            _layoutTimer?.Stop();
            if (_disabled || _stopped) return;
            Refresh();
            _failures = 0;
        }
        catch (Exception exception) { Failed(exception); }
        finally { _queued = false; }
    }

    private string? Match(TabItem tab)
    {
        // pyRevit dev/pyRevitLabs.PyRevit.Runtime/EventHandling.cs, TabColoringTheme.GetTabDocumentId:
        // LayoutDocument.Content -> MFCMDIChildFrameControl.Content -> MFCMDIFrameHost.document.
        // https://github.com/pyrevitlabs/pyRevit/blob/develop/dev/pyRevitLabs.PyRevit.Runtime/EventHandling.cs
        foreach (var initial in new[] { tab.Content, tab.DataContext, tab.Header })
        {
            var value = initial;
            for (var depth = 0; value is not null && depth < 4; depth++)
            {
                foreach (var pair in _documents)
                {
                    if (ReferenceEquals(value, pair.Value.Reference) || ReferenceEquals(Property(value, "Document"), pair.Value.Reference)) return pair.Key;
                }
                if (Property(value, "document") is IntPtr pointer && pointer != IntPtr.Zero)
                    return _documents.FirstOrDefault(candidate => candidate.Value.Pointer == pointer).Key;
                value = Property(value, "Content");
            }
        }
        var title = Property(tab.Content, "Title") as string ?? Property(tab.Header, "Title") as string;
        var matches = _documents.Where(pair => pair.Value.Title == title).ToArray();
        return matches.Length == 1 ? matches[0].Key : null;
    }

    private static object? Property(object? value, string name) => value?.GetType().GetProperty(name)?.GetValue(value);

    private static bool IsDocumentTab(TabItem tab)
    {
        if (tab.GetType().Name == "LayoutDocumentTabItem") return true;
        for (DependencyObject? parent = tab; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent.GetType().Name == "LayoutDocumentPaneControl") return true;
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private void RestoreAll()
    {
        foreach (var pair in _tabs) Restore(pair.Key, pair.Value);
        _tabs.Clear();
    }

    private static void Restore(TabItem tab, SavedTab saved)
    {
        if (TabActivitySettings.ShouldRestore(tab.Background, saved.Brush))
            tab.SetCurrentValue(System.Windows.Controls.Control.BackgroundProperty, saved.PreviousBackground);
        if (TabActivitySettings.ShouldRestore(tab.Foreground, saved.TextBrush))
            tab.SetCurrentValue(System.Windows.Controls.Control.ForegroundProperty, saved.PreviousForeground);
        if (ReferenceEquals(tab.ToolTip, saved.ToolTip)) tab.SetCurrentValue(FrameworkElement.ToolTipProperty, saved.PreviousToolTip);
    }

    private sealed record TabDocument(object Reference, string Title, IntPtr Pointer);

    private sealed class SavedTab(string documentId, TabItem tab)
    {
        public string DocumentId { get; } = documentId;
        public Brush PreviousBackground { get; } = tab.Background;
        public Brush PreviousForeground { get; } = tab.Foreground;
        public object PreviousToolTip { get; } = tab.ToolTip;
        public SolidColorBrush? Brush { get; set; }
        public SolidColorBrush? TextBrush { get; set; }
        public string? ToolTip { get; set; }
    }
}
