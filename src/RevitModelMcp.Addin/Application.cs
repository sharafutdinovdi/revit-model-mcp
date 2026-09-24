using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp;

[UsedImplicitly]
public sealed class Application : ExternalApplication
{
    private static readonly string TriggerFilePath = Path.Combine(
        Output.SnapshotFileWriter.OutputDirectory,
        "trigger.txt");

    private readonly ControlChannel _controlChannel = new(TriggerFilePath);
    private ControlExternalEventHandler? _eventHandler;
    private Autodesk.Revit.UI.ExternalEvent? _externalEvent;
    private ExternalEventRequestQueue? _requestQueue;
    private TriggerFileWatcher? _triggerWatcher;
    private InstanceHeartbeat? _instanceHeartbeat;
    private Document? _activeDocument;
    private HttpChannel? _httpChannel;
    private PipeChannel? _pipeChannel;
    private volatile IReadOnlyList<InstanceDocument> _documents = Array.Empty<InstanceDocument>();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public override void OnStartup()
    {
        PluginLog.Start();
        PluginLog.Info($"RevitModelMcp started. LogPath='{PluginLog.FilePath}'.");
        _eventHandler = new ControlExternalEventHandler(_controlChannel);
        _externalEvent = Autodesk.Revit.UI.ExternalEvent.Create(_eventHandler);
        _requestQueue = new ExternalEventRequestQueue(() =>
        {
            var result = _externalEvent.Raise();
            if (result is ExternalEventRequest.Denied or ExternalEventRequest.TimedOut)
                _controlChannel.Scheduler.MarkWaiting();
            return result is ExternalEventRequest.Accepted or ExternalEventRequest.Pending;
        }, _controlChannel.Scheduler.MarkWaiting);
        _eventHandler.Attach(_requestQueue);
        Directory.CreateDirectory(Output.SnapshotFileWriter.OutputDirectory);
        if (File.Exists(TriggerFilePath))
        {
            File.Move(TriggerFilePath, Path.Combine(Output.SnapshotFileWriter.OutputDirectory, $"stale_{Guid.NewGuid():N}.tmp"));
        }
        _triggerWatcher = new TriggerFileWatcher(
            TriggerFilePath,
            RequestExecution,
            exception => PluginLog.Error("Trigger watcher failed.", exception),
            TimeSpan.FromSeconds(10));
        _triggerWatcher.Start();
        Application.ViewActivated += OnViewActivated;
        Application.ControlledApplication.DocumentClosing += OnDocumentClosing;
        Application.ControlledApplication.DocumentClosed += OnDocumentListChanged;
        Application.ControlledApplication.DocumentOpened += OnDocumentListChanged;
        Application.ControlledApplication.DocumentCreated += OnDocumentListChanged;
        Application.ControlledApplication.DocumentSavedAs += OnDocumentListChanged;
        _activeDocument = RevitContext.UiApplication?.ActiveUIDocument?.Document;
        _documents = ReadDocuments(null);
        try
        {
            _pipeChannel = new PipeChannel(_controlChannel, RequestExecution,
                Application.ControlledApplication.VersionNumber, _instanceId, () => _documents);
            _pipeChannel.Start();
        }
        catch (Exception exception)
        {
            _pipeChannel = null;
            PluginLog.Error("Pipe listener failed; the file channel stays available.", exception);
        }
        // The first heartbeat is written after the pipe listens, so discovery never advertises a dead pipe.
        _instanceHeartbeat = new InstanceHeartbeat(
            Output.SnapshotFileWriter.RootDirectory,
            Process.GetCurrentProcess().Id,
            Application.ControlledApplication.VersionNumber,
            _instanceId,
            _pipeChannel?.PipeName);
        _instanceHeartbeat.Start(_activeDocument, _documents);
        try
        {
            _httpChannel = new HttpChannel(_controlChannel, RequestExecution,
                Application.ControlledApplication.VersionNumber, HttpSettings.Load());
            _httpChannel.UpdateDocument(_activeDocument?.Title);
            _httpChannel.Start();
            _instanceHeartbeat.UpdateHttpPort(_httpChannel.BoundPort);
        }
        catch (Exception exception)
        {
            PluginLog.Warn($"HTTP configuration failed. Check settings.json and its permissions. Type='{exception.GetType().Name}'.");
        }
    }

    public override void OnShutdown()
    {
        Application.ViewActivated -= OnViewActivated;
        Application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        Application.ControlledApplication.DocumentClosed -= OnDocumentListChanged;
        Application.ControlledApplication.DocumentOpened -= OnDocumentListChanged;
        Application.ControlledApplication.DocumentCreated -= OnDocumentListChanged;
        Application.ControlledApplication.DocumentSavedAs -= OnDocumentListChanged;
        _activeDocument = null;
        _instanceHeartbeat?.Dispose();
        _instanceHeartbeat = null;
        _triggerWatcher?.Dispose();
        _triggerWatcher = null;
        _pipeChannel?.Dispose();
        _pipeChannel = null;
        _httpChannel?.Dispose();
        _httpChannel = null;
        _controlChannel.Shutdown();
        _externalEvent?.Dispose();
        _externalEvent = null;
        _requestQueue = null;
        _eventHandler = null;
        PluginLog.Shutdown();
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs args)
    {
        _activeDocument = args.CurrentActiveView?.Document;
        RefreshDocuments(null);
        _httpChannel?.UpdateDocument(_activeDocument?.Title);
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        if (ReferenceEquals(args.Document, _activeDocument))
        {
            // The heartbeat must clear a closed active document without waiting for a view switch.
            _activeDocument = null;
            _httpChannel?.UpdateDocument(null);
        }
        RefreshDocuments(args.Document);
    }

    private void OnDocumentListChanged(object? sender, EventArgs args) => RefreshDocuments(null);

    private void RefreshDocuments(Document? closing)
    {
        try
        {
            _documents = ReadDocuments(closing);
        }
        catch (Exception exception)
        {
            // Discovery data must never break a Revit event.
            PluginLog.Error("Open document list could not be read.", exception);
        }
        _instanceHeartbeat?.UpdateDocument(_activeDocument, _documents);
    }

    private IReadOnlyList<InstanceDocument> ReadDocuments(Document? closing)
    {
        var documents = new List<InstanceDocument>();
        var application = RevitContext.UiApplication?.Application;
        if (application is null) return documents;
        foreach (Document document in application.Documents)
        {
            if (document.IsLinked || (closing is not null && document.Equals(closing))) continue;
            documents.Add(new InstanceDocument
            {
                Title = document.Title,
                Path = document.PathName ?? string.Empty,
                IsActive = _activeDocument is not null && document.Equals(_activeDocument),
                IsFamilyDocument = document.IsFamilyDocument
            });
        }
        return documents;
    }

    private void RequestExecution()
    {
        try
        {
            _controlChannel.ScanPendingFiles();
            // Raise is allowed on the watcher thread; Revit API calls run only inside Execute.
            _requestQueue?.Request();
        }
        catch (Exception exception)
        {
            PluginLog.Error("External event request failed.", exception);
        }
    }
}

internal sealed class InstanceHeartbeat : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(1);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly int _processId;
    private readonly string _revitVersion;
    private readonly string _instanceId;
    private readonly string? _pipeName;
    private IReadOnlyList<InstanceDocument> _documents = Array.Empty<InstanceDocument>();
    private System.Threading.Timer? _timer;
    private string _documentTitle = string.Empty;
    private string _documentPath = string.Empty;
    private bool _disposed;
    private int? _httpPort;

    public InstanceHeartbeat(string directory, int processId, string revitVersion, string instanceId, string? pipeName)
    {
        _directory = directory;
        _processId = processId;
        _revitVersion = revitVersion;
        _instanceId = instanceId;
        _pipeName = pipeName;
        _path = Path.Combine(directory, $"instance_{processId}.json");
        _temporaryPath = Path.Combine(directory, $"instance_{processId}.tmp");
    }

    public void Start(Document? document, IReadOnlyList<InstanceDocument> documents)
    {
        UpdateDocument(document, documents);
    }

    public void UpdateDocument(Document? document, IReadOnlyList<InstanceDocument> documents)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _documentTitle = document?.Title ?? string.Empty;
            _documentPath = document?.PathName ?? string.Empty;
            _documents = documents;
            WriteStatus();
            _timer ??= new System.Threading.Timer(_ => Tick(), null, HeartbeatInterval, HeartbeatInterval);
            _timer.Change(HeartbeatInterval, HeartbeatInterval);
        }
    }

    public void UpdateHttpPort(int? port)
    {
        lock (_sync)
        {
            _httpPort = port;
            if (!_disposed) WriteStatus();
        }
    }

    private void Tick()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            WriteStatus();
        }
    }

    private void WriteStatus()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var status = new InstanceStatus
            {
                ProcessId = _processId,
                RevitVersion = _revitVersion,
                DocumentTitle = _documentTitle,
                DocumentPath = _documentPath,
                UpdatedUtc = DateTime.UtcNow.ToString("O"),
                StartedUtc = Output.SnapshotFileWriter.StartedUtc,
                HttpPort = _httpPort,
                InstanceId = _instanceId,
                PipeName = _pipeName,
                Protocols = Protocols(),
                Documents = _documents.ToList()
            };
            File.WriteAllText(_temporaryPath, InstanceStatusJsonSerializer.Serialize(status), Utf8WithoutBom);
            if (File.Exists(_path))
            {
                File.Replace(_temporaryPath, _path, null);
            }
            else
            {
                File.Move(_temporaryPath, _path);
            }

            DeleteStaleFiles();
        }
        catch (Exception exception)
        {
            // Heartbeat failures must not interrupt add-in loading or operations.
            PluginLog.Error($"Instance heartbeat write failed. Path='{_path}'.", exception);
        }
    }

    private List<string> Protocols()
    {
        var protocols = new List<string>();
        if (_pipeName is not null) protocols.Add(PipeProtocol.Version);
        protocols.Add("file/2");
        if (_httpPort is not null) protocols.Add("http/1");
        return protocols;
    }

    private void DeleteStaleFiles()
    {
        var staleBefore = DateTime.UtcNow - StaleAge;
        foreach (var candidate in Directory.EnumerateFiles(_directory, "instance_*.json"))
        {
            if (!string.Equals(candidate, _path, StringComparison.OrdinalIgnoreCase) &&
                File.GetLastWriteTimeUtc(candidate) < staleBefore)
            {
                try
                {
                    // After a Revit crash, only the next running instance can delete the file.
                    File.Delete(candidate);
                }
                catch (Exception exception)
                {
                    PluginLog.Error($"Stale instance heartbeat delete failed. Path='{candidate}'.", exception);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            try
            {
                File.Delete(_temporaryPath);
                File.Delete(_path);
            }
            catch (Exception exception)
            {
                // The file expires after one minute if the OS prevents deletion during shutdown.
                PluginLog.Error($"Instance heartbeat shutdown cleanup failed. Path='{_path}'.", exception);
            }
        }
    }
}

internal sealed class ControlExternalEventHandler : IExternalEventHandler
{
    private readonly ControlChannel _controlChannel;
    private ExternalEventRequestQueue? _requestQueue;

    public ControlExternalEventHandler(ControlChannel controlChannel)
    {
        _controlChannel = controlChannel;
    }

    public void Attach(ExternalEventRequestQueue requestQueue)
    {
        _requestQueue = requestQueue;
    }

    public void Execute(UIApplication application)
    {
        var requestQueue = _requestQueue
                           ?? throw new InvalidOperationException("The ExternalEvent queue is not initialized.");
        requestQueue.Execute(
            () =>
            {
                _controlChannel.Tick(application);
                if (_controlChannel.HasPendingWork)
                {
                    // The batch session requests the next Execute independently of Idling.
                    requestQueue.Request();
                }
            },
            exception => _controlChannel.HandleUnhandledException(application, exception));
    }

    public string GetName() => "RevitModelMcp control channel";
}
