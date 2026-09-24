using Autodesk.Revit.UI;

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

    public static void Configure(bool enabled)
    {
        lock (SyncRoot) _enabled = enabled;
    }

    public static void Reset()
    {
        lock (SyncRoot) _shown = false;
    }

    public static void EnsureShown()
    {
        lock (SyncRoot)
        {
            if (_shown || !_enabled) return;
            _shown = true;
        }
        try
        {
            var pane = new DockablePane(ActivityPaneProvider.PaneId);
            if (!pane.IsShown()) pane.Show();
        }
        catch (Exception exception)
        {
            PluginLog.Error("Could not show the MCP activity pane automatically.", exception);
        }
    }
}
