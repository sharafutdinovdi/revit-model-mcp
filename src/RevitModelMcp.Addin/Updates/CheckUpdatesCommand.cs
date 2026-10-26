using Autodesk.Revit.Attributes;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Activity;

namespace RevitModelMcp.Updates;

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class CheckUpdatesCommand : ExternalCommand
{
    public override void Execute()
    {
        var pane = new Autodesk.Revit.UI.DockablePane(ActivityPaneProvider.PaneId);
        pane.Show();
        UpdateService.CheckNow();
    }
}
