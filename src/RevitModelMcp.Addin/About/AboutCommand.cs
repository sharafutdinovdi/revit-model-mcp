using Autodesk.Revit.Attributes;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;

namespace RevitModelMcp.About;

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class AboutCommand : ExternalCommand
{
    public override void Execute() => new AboutWindow(Application.Application.VersionNumber).ShowDialog();
}
