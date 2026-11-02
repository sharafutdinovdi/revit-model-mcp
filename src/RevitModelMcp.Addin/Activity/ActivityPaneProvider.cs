using Autodesk.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.Decorators;
using RevitModelMcp.Control;

namespace RevitModelMcp.Activity;

/// <summary>Registers the MCP activity dockable pane during application startup.</summary>
internal static class ActivityPaneProvider
{
    public static readonly DockablePaneId PaneId = new(new Guid("6F1D9A2E-6B3E-4C7A-9C7E-7C6C6E2B6F31"));

    public static void Register(UIControlledApplication application)
    {
        PaneTheme.Register(application);
        DockablePaneProvider
            .Register(application, PaneId, PaneText.Caption)
            .SetConfiguration(data =>
            {
                data.FrameworkElement = new ActivityPaneView();
                data.InitialState = new DockablePaneState
                {
                    MinimumWidth = 300,
                    DockPosition = DockPosition.Right
                };
            });
    }
}

[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
[UsedImplicitly]
public sealed class ShowActivityPaneCommand : Autodesk.Revit.UI.IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
    {
        if (!DockablePane.PaneIsRegistered(ActivityPaneProvider.PaneId) ||
            !DockablePane.PaneExists(ActivityPaneProvider.PaneId))
        {
            var entries = ActivityLog.Snapshot();
            var recent = entries.Take(10);
            var notices = entries.Where(entry => entry.Command == "system-notice");
            var content = string.Join("\n\n", recent.Concat(notices).Distinct()
                .Select(entry => $"{entry.Time:t} [{entry.State}] {entry.Summary}"));
            AppDialog.Show(PaneText.Caption,
                "The activity pane opens once a model or view is open.\n\n" +
                (content.Length > 0 ? content : PaneText.EmptyTitle + "\n" + PaneText.EmptyHint));
            return Result.Succeeded;
        }

        var pane = new DockablePane(ActivityPaneProvider.PaneId);
        if (pane.IsShown()) pane.Hide();
        else pane.Show();
        return Result.Succeeded;
    }
}
