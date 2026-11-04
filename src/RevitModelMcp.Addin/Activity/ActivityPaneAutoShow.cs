using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Nice3point.Revit.Toolkit;

namespace RevitModelMcp.Activity;

/// <summary>
/// Shows the "MCP activity" dockable pane the first time an action runs in a Revit session, so the
/// user sees changes live without hunting for the ribbon button. Controlled by
/// <c>showActivityPaneOnAction</c> in <c>%LOCALAPPDATA%\RevitModelMcp\settings.json</c> (default true).
/// </summary>
internal static class ActivityPaneAutoShow
{
    private static readonly object SyncRoot = new();
    private static bool _shown;
    private static bool _enabled = true;
    private static UIApplication? _pendingApplication;

    public static void Configure(bool enabled)
    {
        lock (SyncRoot) _enabled = enabled;
    }

    public static void Reset()
    {
        lock (SyncRoot)
        {
            _shown = false;
            ClearPendingShow();
        }
    }

    public static void EnsureShown()
    {
        lock (SyncRoot)
        {
            if (_shown || !_enabled) return;
        }
        try
        {
            if (!DockablePane.PaneIsRegistered(ActivityPaneProvider.PaneId) ||
                !DockablePane.PaneExists(ActivityPaneProvider.PaneId))
            {
                if (_pendingApplication is null && RevitContext.UiApplication is { } application)
                {
                    _pendingApplication = application;
                    application.ViewActivated += OnViewActivated;
                }
                return;
            }

            var pane = new DockablePane(ActivityPaneProvider.PaneId);
            if (!pane.IsShown()) pane.Show();
            lock (SyncRoot)
            {
                _shown = true;
                ClearPendingShow();
            }
        }
        catch (Exception exception)
        {
            PluginLog.Error("Could not show the MCP activity pane automatically.", exception);
        }
    }

    private static void OnViewActivated(object? sender, ViewActivatedEventArgs args) => EnsureShown();

    private static void ClearPendingShow()
    {
        if (_pendingApplication is null) return;
        _pendingApplication.ViewActivated -= OnViewActivated;
        _pendingApplication = null;
    }
}
