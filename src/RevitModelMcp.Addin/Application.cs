using System.IO;
using System.Diagnostics;
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

    public override void OnStartup()
    {
        PluginLog.Start();
        PluginLog.Info($"RevitModelMcp started. LogPath='{PluginLog.FilePath}'.");
        _eventHandler = new ControlExternalEventHandler(_controlChannel);
        _externalEvent = Autodesk.Revit.UI.ExternalEvent.Create(_eventHandler);
        _requestQueue = new ExternalEventRequestQueue(() => _externalEvent.Raise());
        _eventHandler.Attach(_requestQueue);
        _triggerWatcher = new TriggerFileWatcher(
            TriggerFilePath,
            RequestExecution,
            exception => PluginLog.Error("Trigger watcher failed.", exception),
            TimeSpan.FromSeconds(10));
        _triggerWatcher.Start();
        _instanceHeartbeat = new InstanceHeartbeat(
            Path.GetDirectoryName(TriggerFilePath)!,
            Process.GetCurrentProcess().Id,
            Application.ControlledApplication.VersionNumber);
        Application.ViewActivated += OnViewActivated;
        Application.ControlledApplication.DocumentClosing += OnDocumentClosing;
        _activeDocument = RevitContext.UiApplication?.ActiveUIDocument?.Document;
        _instanceHeartbeat.Start(_activeDocument);
    }

    public override void OnShutdown()
    {
        Application.ViewActivated -= OnViewActivated;
        Application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        _activeDocument = null;
        _instanceHeartbeat?.Dispose();
        _instanceHeartbeat = null;
        _triggerWatcher?.Dispose();
        _triggerWatcher = null;
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
        _instanceHeartbeat?.UpdateDocument(_activeDocument);
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        if (ReferenceEquals(args.Document, _activeDocument))
        {
            // Закрытый активный документ нельзя оставлять в heartbeat до следующего переключения вида.
            _activeDocument = null;
            _instanceHeartbeat?.UpdateDocument(null);
        }
    }

    private void RequestExecution()
    {
        try
        {
            // Raise разрешён с потока watcher; Revit API вызывается только внутри Execute.
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
    private System.Threading.Timer? _timer;
    private string _documentTitle = string.Empty;
    private string _documentPath = string.Empty;
    private bool _disposed;

    public InstanceHeartbeat(string directory, int processId, string revitVersion)
    {
        _directory = directory;
        _processId = processId;
        _revitVersion = revitVersion;
        _path = Path.Combine(directory, $"instance_{processId}.json");
        _temporaryPath = Path.Combine(directory, $"instance_{processId}.tmp");
    }

    public void Start(Document? document)
    {
        UpdateDocument(document);
    }

    public void UpdateDocument(Document? document)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _documentTitle = document?.Title ?? string.Empty;
            _documentPath = document?.PathName ?? string.Empty;
            WriteStatus();
            _timer ??= new System.Threading.Timer(_ => Tick(), null, HeartbeatInterval, HeartbeatInterval);
            _timer.Change(HeartbeatInterval, HeartbeatInterval);
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
                UpdatedUtc = DateTime.UtcNow.ToString("O")
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
            // Heartbeat не должен мешать загрузке и основной работе плагина.
            PluginLog.Error($"Instance heartbeat write failed. Path='{_path}'.", exception);
        }
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
                    // После аварии Revit удалить файл может только следующий живой экземпляр.
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
                // Файл протухнет за минуту, если ОС не дала удалить его при выгрузке.
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
                           ?? throw new InvalidOperationException("Очередь ExternalEvent не инициализирована.");
        requestQueue.Execute(
            () =>
            {
                _controlChannel.Tick(application);
                if (_controlChannel.HasActiveSession)
                {
                    // Порционная сессия просит следующий Execute без зависимости от Idling.
                    requestQueue.Request();
                }
            },
            exception => _controlChannel.HandleUnhandledException(application, exception));
    }

    public string GetName() => "RevitModelMcp control channel";
}
