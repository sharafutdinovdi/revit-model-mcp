using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using JetBrains.Annotations;
using Nice3point.Revit.Extensions;
using Nice3point.Revit.Toolkit;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Activity;
using RevitModelMcp.Connect;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Export;
using RevitModelMcp.Core.Help;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;
using RevitModelMcp.Updates;

namespace RevitModelMcp;

[UsedImplicitly]
public sealed class Application : ExternalApplication
{
    internal static string AddinVersion => typeof(Application).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal const int ProtocolVersion = 1;

    internal static readonly string[] SupportedCommands =
    [
        "ping", "set-read-only", "jobs", "jobs/persisted", "model-health", "links-status", "shared-coordinates",
        "parameter-fill-check", "document-info", "documents", "ui-state", "list-views", "view-summary",
        "view-info", "view-elements", "element-details", "view-warnings", "export-view", "capture-elements", "schedule-data",
        "query-elements", "aggregate-elements", "list-catalog", "list-warnings",
        "list-relations", "family-audit", "nwc-settings-check", "compare-link-datums",
        "select", "show", "isolate", "override-graphics", "move", "rotate", "copy", "mirror", "change-type", "update-parameters", "place-family", "load-family", "place-families", "create-wall", "link-cad", "walls-from-cad", "create-mep-run", "create-view", "duplicate-view", "apply-view-template", "create-sheet", "place-views-on-sheet", "tag-elements", "create-schedule",
        "set-parameter", "delete", "batch", "process-models", "export-nwc", "export", "edit-families",
        "align-link-datums", "open-document", "close-document", "save-document",
        "sync-document", "activate-document", "activate-view", "close-views", "new-document",
        "list-sheets", "set-view-visibility", "remove-links", "execute-code", "api-lookup", "undo-last",
        "batch-supervisor-start", "batch-prepass", "batch-open", "batch-close", "model-snapshot"
    ];
    private readonly List<(RibbonButton Button, string Icon)> _ribbonButtons = [];
    private ReadOnlyRibbon? _readOnlyRibbon;
    private ControlChannel _controlChannel = null!;
    private ControlExternalEventHandler? _eventHandler;
    private Autodesk.Revit.UI.ExternalEvent? _externalEvent;
    private ExternalEventRequestQueue? _requestQueue;
    private JobFileWatcher? _jobWatcher;
    private InstanceHeartbeat? _instanceHeartbeat;
    private Document? _activeDocument;
    private HttpChannel? _httpChannel;
    private PipeChannel? _pipeChannel;
    private volatile IReadOnlyList<InstanceDocument> _documents = Array.Empty<InstanceDocument>();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private static Application? _instance;

    public override void OnStartup()
    {
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve += ResolveCodeDependency;
#endif
        Policy.PolicyProvider.ApplyProcessEnvironment();
        PluginLog.Start();
        _instance = this;
        PluginLog.Info($"RevitModelMcp started. LogPath='{PluginLog.FilePath}'.");
        var (fileChannelEnabled, fileChannelWarning) = Output.ChannelDirectory.InitializeChannel();
        if (fileChannelWarning is not null)
            PluginLog.Warn(fileChannelWarning);
        _controlChannel = new ControlChannel(Output.ChannelDirectory.OutputDirectory)
        {
            FileChannelEnabled = fileChannelEnabled
        };
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
        NwcPathValidator.ConfigureTrustedNetworkRoots([]);
        HttpSettings? httpSettings = null;
        string? httpSettingsError = null;
        var showActivityPaneOnAction = true;
        try
        {
            httpSettings = HttpSettings.Load();
            if (Policy.PolicyProvider.Current.HttpEnabled is { } policyHttp) httpSettings.HttpEnabled = policyHttp;
            showActivityPaneOnAction = httpSettings.ShowActivityPaneOnAction;
            try
            {
                NwcPathValidator.ConfigureTrustedNetworkRoots(Policy.PolicyProvider.Current.TrustedNetworkRoots ?? httpSettings.TrustedNetworkRoots);
            }
            catch (ArgumentException)
            {
                PluginLog.Warn("Invalid trustedNetworkRoots in settings.json. Network shares remain unavailable.");
            }
        }
        catch (Exception exception)
        {
            httpSettingsError = exception.GetType().Name;
            PluginLog.Warn($"Settings could not be loaded. Check settings.json and its permissions. Type='{exception.GetType().Name}'.");
        }
        if (_controlChannel.FileChannelEnabled)
        {
            _jobWatcher = new JobFileWatcher(
                Output.ChannelDirectory.OutputDirectory,
                RequestExecution,
                exception => PluginLog.Error("Job file watcher failed.", exception),
                TimeSpan.FromSeconds(10));
            _jobWatcher.Start();
        }
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
            PluginLog.Error("Pipe listener failed.", exception);
        }
        if (_controlChannel.FileChannelEnabled)
        {
            // The first heartbeat is written after the pipe listens, so discovery never advertises a dead pipe.
            _instanceHeartbeat = new InstanceHeartbeat(
                Output.ChannelDirectory.RootDirectory,
                Process.GetCurrentProcess().Id,
                Application.ControlledApplication.VersionNumber,
                _instanceId,
                _pipeChannel?.PipeName);
        }
        Application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        ActivityHost.Scheduler = _controlChannel.Scheduler;
        ActivityHost.CancelJob = _controlChannel.CancelJob;
        RegisterActivityPane();
        _instanceHeartbeat?.Start(_activeDocument, _documents);
        var httpStatus = httpSettings is null
            ? HttpListenerStatus.ConfigError(httpSettingsError!)
            : HttpListenerStatus.ForDisabled(Policy.PolicyProvider.Current.HttpEnabled);
        var channelOwnsStatus = false;
        if (httpSettings is not null)
        {
            try
            {
                httpSettings.ApplyHttpOverrides(Policy.PolicyProvider.Current.HttpEnabled);
                _httpChannel = new HttpChannel(_controlChannel, RequestExecution,
                    Application.ControlledApplication.VersionNumber, httpSettings);
                _httpChannel.UpdateDocument(_activeDocument?.Title);
                _httpChannel.StatusChanged += status => _instanceHeartbeat?.UpdateHttpStatus(status);
                _httpChannel.Start();
                _instanceHeartbeat?.UpdateHttpPort(_httpChannel.BoundPort);
                // The channel pushes its own later status changes, including the self-probe result.
                channelOwnsStatus = httpSettings.HttpEnabled;
            }
            catch (Exception exception)
            {
                httpStatus = HttpListenerStatus.ConfigError(exception.GetType().Name);
                PluginLog.Warn($"HTTP configuration failed. Check settings.json and its permissions. Type='{exception.GetType().Name}'.");
            }
        }
        if (!channelOwnsStatus) _instanceHeartbeat?.UpdateHttpStatus(httpStatus);
        ActivityPaneAutoShow.Configure(showActivityPaneOnAction);
        _controlChannel.ConfigureTabActivity(httpSettings?.TabActivityHighlight ?? true);
        UpdateService.Start();
    }

    public override void OnShutdown()
    {
        PaneTheme.Changed -= RefreshRibbonImages;
        _ribbonButtons.Clear();
        _readOnlyRibbon?.Dispose();
        ActionCommandExecutor.ReadOnlyLock.Dispose();
        RibbonBranding.Detach();
        _instance = null;
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve -= ResolveCodeDependency;
#endif
        Application.ViewActivated -= OnViewActivated;
        Application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        Application.ControlledApplication.DocumentClosed -= OnDocumentListChanged;
        Application.ControlledApplication.DocumentOpened -= OnDocumentListChanged;
        Application.ControlledApplication.DocumentCreated -= OnDocumentListChanged;
        Application.ControlledApplication.DocumentSavedAs -= OnDocumentListChanged;
        Application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        ActivityHost.Reset();
        ActivityPaneAutoShow.Reset();
        _activeDocument = null;
        _instanceHeartbeat?.Dispose();
        _instanceHeartbeat = null;
        _jobWatcher?.Dispose();
        _jobWatcher = null;
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

#if NETFRAMEWORK
    private static Assembly? ResolveCodeDependency(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name is not ("Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp" or
            "System.Buffers" or "System.Collections.Immutable" or "System.Memory" or
            "System.Numerics.Vectors" or "System.Reflection.Metadata" or
            "System.Runtime.CompilerServices.Unsafe" or "System.Text.Encoding.CodePages" or
            "System.Threading.Tasks.Extensions")) return null;
        var folder = Path.GetDirectoryName(typeof(Application).Assembly.Location)!;
        var path = Path.Combine(folder, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
#endif

    private void RegisterActivityPane()
    {
        ActivityPaneProvider.Register(Application);
        var panel = Application.CreatePanel(RibbonText.TabName, RibbonText.TabName);
        var brand = panel.AddPullDownButton(RibbonText.TabName);
        ConfigureRibbonButton(brand, "Logo", RibbonText.ModelMcpToolTip, RibbonText.ModelMcpDescription, HelpLinks.AddinHelpUrl);
        ConfigureRibbonButton(brand.AddPushButton<About.AboutCommand>("About"), "About",
            RibbonText.AboutToolTip, RibbonText.AboutDescription, HelpLinks.AddinHelpUrl);
        if (UpdateService.Channel() == Core.Updates.UpdateChannel.Release)
            ConfigureRibbonButton(brand.AddPushButton<CheckUpdatesCommand>("Check for updates"), "Update",
                RibbonText.UpdateToolTip, RibbonText.UpdateDescription, HelpLinks.AddinHelpUrl);
        ConfigureRibbonButton(brand.AddPushButton<OpenSettingsFolderCommand>("Settings"), "Settings",
            RibbonText.SettingsToolTip, RibbonText.SettingsDescription, HelpLinks.AddinHelpUrl);
        ConfigureRibbonButton(panel.AddPushButton<ConnectAiCommand>(ConnectText.RibbonButton), "Connect",
            RibbonText.ConnectToolTip, RibbonText.ConnectDescription, HelpLinks.AddinHelpUrl);
        ConfigureRibbonButton(panel.AddPushButton<ShowActivityPaneCommand>(PaneText.RibbonButton), "Activity",
            RibbonText.ActivityToolTip, RibbonText.ActivityDescription, HelpLinks.AddinHelpUrl);
        var items = panel.AddStackedItems(
            UrlButtonData<HelpCommand>("Help"),
            UrlButtonData<ReportIssueCommand>("Report issue"),
            UrlButtonData<SupportDeveloperCommand>("Support developer"));
        ConfigureRibbonButton((RibbonButton)items[0], "Help", RibbonText.HelpToolTip, RibbonText.HelpDescription, HelpLinks.Documentation);
        ConfigureRibbonButton((RibbonButton)items[1], "Issue", RibbonText.IssueToolTip, RibbonText.IssueDescription, HelpLinks.Issues);
        ConfigureRibbonButton((RibbonButton)items[2], "Support", RibbonText.SupportToolTip, RibbonText.SupportDescription, HelpLinks.Support);
        PaneTheme.Changed += RefreshRibbonImages;
        _readOnlyRibbon = new ReadOnlyRibbon();
        RibbonBranding.ApplyPanelBackground();
        RibbonBranding.ApplyTabIcon();
    }

    private static PushButtonData UrlButtonData<TCommand>(string text) where TCommand : IExternalCommand =>
        new(typeof(TCommand).Name, text, typeof(TCommand).Assembly.Location, typeof(TCommand).FullName);

    private void ConfigureRibbonButton(RibbonButton button, string icon, string tooltip, string description, string helpUrl)
    {
        _ribbonButtons.Add((button, icon));
        ApplyRibbonImages(button, icon);
        button.ToolTip = tooltip;
        button.LongDescription = description;
        button.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, helpUrl));
    }

    private void RefreshRibbonImages()
    {
        foreach (var (button, icon) in _ribbonButtons)
            ApplyRibbonImages(button, icon);
    }

    private static void ApplyRibbonImages(RibbonButton button, string icon)
    {
        var suffix = PaneTheme.IsDark ? "-dark" : string.Empty;
        button.SetImage($"/RevitModelMcp;component/Resources/Icons/{icon}16{suffix}.png");
        button.SetLargeImage($"/RevitModelMcp;component/Resources/Icons/{icon}32{suffix}.png");
        button.ToolTipImage = new System.Windows.Media.Imaging.BitmapImage(
            new Uri($"pack://application:,,,/RevitModelMcp;component/Resources/Icons/{icon}Tooltip{suffix}.png"));
    }

    /// <summary>Re-reads document titles, for example after a detached open appended "_detached" with no later event.</summary>
    internal static void RefreshDocumentList()
    {
        var instance = _instance;
        if (instance is null) return;
        instance.RefreshDocuments(null);
        instance._httpChannel?.UpdateDocument(instance._activeDocument?.Title);
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs args)
    {
        _activeDocument = args.CurrentActiveView?.Document;
        RefreshDocuments(null);
        _httpChannel?.UpdateDocument(_activeDocument?.Title);
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        ConfirmationStore.DocumentClosing(args.Document);
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

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        ConfirmationStore.DocumentChanged(args.GetDocument());
        UndoTracker.OnDocumentChanged(args);
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
    private HttpListenerStatus? _httpStatus;
    private int _heartbeatWriteFailures;

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

    public void UpdateHttpStatus(HttpListenerStatus status)
    {
        lock (_sync)
        {
            _httpStatus = status;
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
                StartedUtc = Output.ChannelDirectory.StartedUtc,
                HttpPort = _httpPort,
                HttpState = _httpStatus?.State,
                HttpReason = _httpStatus?.Reason,
                InstanceId = _instanceId,
                PipeName = _pipeName,
                Protocols = Protocols(),
                Documents = _documents.ToList(),
                AddinVersion = Application.AddinVersion,
                ProtocolVersion = Application.ProtocolVersion,
                Commands = Application.SupportedCommands.ToList()
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

            _heartbeatWriteFailures = 0;
            DeleteStaleFiles();
        }
        catch (IOException)
        {
            if (++_heartbeatWriteFailures > 1)
                PluginLog.Warn("Instance heartbeat write failed repeatedly. Retrying on the next tick.");
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
        if (_httpPort is not null && _httpStatus?.IsFailed != true) protocols.Add("http/1");
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

internal static class RibbonText
{
    internal const string TabName = "Model MCP";

    internal const string ModelMcpToolTip = "Open product information, updates and settings.";
    internal const string ModelMcpDescription =
        "Open the menu for information about your Model MCP installation and its local settings. " +
        "Use About to identify your version when asking for help. Check for updates is available for release installations.";

    internal const string AboutToolTip = "View your Model MCP installation details.";
    internal const string AboutDescription =
        "Open a window with the installed Model MCP version, Revit year and update channel. " +
        "Use these details to check which installation you are running or include them in a support request.";

    internal const string UpdateToolTip = "Check for a newer Model MCP release.";
    internal const string UpdateDescription =
        "Check for a newer release using your configured update policy and show the result in Activity. " +
        "Use this to find available fixes and improvements. Installation follows the update policy set for this workstation.";

    internal const string SettingsToolTip = "Open the local settings folder in Explorer.";
    internal const string SettingsDescription =
        "Open the folder containing this workstation's settings and policy files in Windows Explorer. " +
        "Use these files to review or adjust Model MCP behaviour. " +
        "The tabActivityHighlight setting controls document tab indicators and requires a Revit restart after changes.";

    internal const string ConnectToolTip = "Set up an AI app to work with this Revit workstation.";
    internal const string ConnectDescription =
        "Open the connection window to add or remove Model MCP in a supported AI app's settings, or copy the Claude Code command. " +
        "This lets your AI app use the models open on this Revit workstation. " +
        "Existing settings are backed up before changes. Restart the AI app after setup.";

    internal const string ActivityToolTip = "Show or hide the MCP activity pane.";
    internal const string ActivityDescription =
        "Show or hide the pane that lists AI requests, queued work and changes to the model. " +
        "Review affected elements and results to understand what happened. " +
        "Use the pane's undo controls when you need to reverse a model change.";

    internal const string HelpToolTip = "Open the Model MCP documentation in your browser.";
    internal const string HelpDescription =
        "Open the online documentation in your default browser. " +
        "Find setup instructions, available tools and guidance for working with Revit models through an AI app.";

    internal const string IssueToolTip = "Open GitHub to report a problem with Model MCP.";
    internal const string IssueDescription =
        "Open the project's GitHub issues page in your default browser. " +
        "Check existing reports or describe a problem you encountered. " +
        "Include your Model MCP version, Revit year and steps to reproduce it to help diagnose the issue.";

    internal const string SupportToolTip = "Support Model MCP development through Ko-fi.";
    internal const string SupportDescription =
        "Open the developer's Ko-fi page in your default browser. " +
        "Model MCP is free and open source. Your contribution helps fund maintenance, fixes and new features.";
}

internal static class RibbonBranding
{
    private const string TabIconName = "ModelMcpTabIcon";
    private static Autodesk.Windows.RibbonControl? _ribbon;
    private static long _lastLayoutCheck;
    private static bool _applying;
    private static bool _missingHeaderLogged;

    internal static void ApplyTabIcon()
    {
        if (_applying) return;
        _applying = true;
        try
        {
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
            if (ribbon is null) return;
            if (!ReferenceEquals(_ribbon, ribbon))
            {
                Detach();
                _ribbon = ribbon;
                ribbon.Loaded += OnRibbonLoaded;
                ribbon.LayoutUpdated += OnRibbonLayoutUpdated;
                PaneTheme.Changed += OnThemeChanged;
            }
            var tab = ribbon.Tabs.FirstOrDefault(tab => tab.Title == RibbonText.TabName);
            if (tab is null) return;
            var stack = Descendants(ribbon)
                .OfType<System.Windows.FrameworkElement>()
                .Where(element => ReferenceEquals(element.DataContext, tab))
                .SelectMany(Descendants)
                .OfType<System.Windows.Controls.StackPanel>()
                .FirstOrDefault(element => element.Name == "mStackPanel");
            if (stack is null)
            {
                if (!_missingHeaderLogged)
                    PluginLog.Debug("Ribbon tab header mStackPanel is not realised; the plain title remains available.");
                _missingHeaderLogged = true;
                return;
            }
            _missingHeaderLogged = false;
            var icon = stack.Children.OfType<System.Windows.Controls.Image>()
                .FirstOrDefault(image => image.Name == TabIconName);
            var suffix = PaneTheme.IsDark ? "-dark" : string.Empty;
            var uri = new Uri($"pack://application:,,,/RevitModelMcp;component/Resources/Icons/Logo16{suffix}.png");
            if (icon is null)
            {
                icon = new System.Windows.Controls.Image
                {
                    Name = TabIconName,
                    Width = 16,
                    Height = 16,
                    Margin = new System.Windows.Thickness(0, 0, 4, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Focusable = false,
                    Source = new System.Windows.Media.Imaging.BitmapImage(uri)
                };
                stack.Children.Insert(0, icon);
            }
            else if (icon.Source is not System.Windows.Media.Imaging.BitmapImage bitmap || bitmap.UriSource != uri)
            {
                icon.Source = new System.Windows.Media.Imaging.BitmapImage(uri);
            }
        }
        catch (Exception exception)
        {
            PluginLog.Debug("Ribbon tab icon could not be applied; the plain title remains available.", exception);
        }
        finally
        {
            _applying = false;
        }
    }

    internal static void Detach()
    {
        if (_ribbon is null) return;
        _ribbon.Loaded -= OnRibbonLoaded;
        _ribbon.LayoutUpdated -= OnRibbonLayoutUpdated;
        PaneTheme.Changed -= OnThemeChanged;
        _ribbon = null;
    }

    private static void OnRibbonLoaded(object sender, System.Windows.RoutedEventArgs args) => ApplyTabIcon();

    private static void OnRibbonLayoutUpdated(object? sender, EventArgs args)
    {
        var timestamp = Stopwatch.GetTimestamp();
        if (timestamp - _lastLayoutCheck < Stopwatch.Frequency / 4) return;
        _lastLayoutCheck = timestamp;
        ApplyTabIcon();
    }

    private static void OnThemeChanged()
    {
        ApplyPanelBackground();
        ApplyTabIcon();
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static void ApplyPanelBackground()
    {
        try
        {
            var tab = Autodesk.Windows.ComponentManager.Ribbon.Tabs.First(tab => tab.Title == RibbonText.TabName);
            var panel = tab.Panels.First(panel => panel.Source.Title == RibbonText.TabName);
            var titleBackground = PaneTheme.IsDark
                ? System.Windows.Media.Color.FromRgb(0x1F, 0x4E, 0x79)
                : System.Windows.Media.Color.FromRgb(0xCF, 0xE3, 0xF7);
            panel.CustomPanelTitleBarBackground = new System.Windows.Media.SolidColorBrush(titleBackground);
        }
        catch (Exception exception)
        {
            PluginLog.Debug("Ribbon panel branding could not be applied.", exception);
        }
    }
}
