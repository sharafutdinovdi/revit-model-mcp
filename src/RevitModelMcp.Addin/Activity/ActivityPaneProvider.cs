using Autodesk.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.Decorators;

namespace RevitModelMcp.Activity;

/// <summary>Registers the "MCP activity" dockable pane during application startup.</summary>
internal static class ActivityPaneProvider
{
    public static readonly DockablePaneId PaneId = new(new Guid("6F1D9A2E-6B3E-4C7A-9C7E-7C6C6E2B6F31"));

    public static void Register(UIControlledApplication application)
    {
        DockablePaneProvider
            .Register(application, PaneId, "MCP activity")
            .SetConfiguration(data =>
            {
                data.FrameworkElement = new ActivityPaneView();
                data.InitialState = new DockablePaneState
                {
                    MinimumWidth = 320,
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
        var pane = new DockablePane(ActivityPaneProvider.PaneId);
        if (pane.IsShown()) pane.Hide();
        else pane.Show();
        return Result.Succeeded;
    }
}
