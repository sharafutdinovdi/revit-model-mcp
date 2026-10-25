using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Core.Policy;

namespace RevitModelMcp;

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class OpenSettingsFolderCommand : ExternalCommand
{
    public override void Execute()
    {
        var directory = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(CodeExecutionGate.SettingsPath))!;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
}
