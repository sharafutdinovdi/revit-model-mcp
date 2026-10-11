using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JetBrains.Annotations;

namespace RevitModelMcp.Connect;

/// <summary>Opens the dialog that connects AI apps on this workstation to the local server.</summary>
[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class ConnectAiCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var window = new ConnectAiWindow();
            _ = new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
            window.ShowDialog();
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            PluginLog.Error("Connect AI app dialog failed.", exception);
            message = "The Connect AI app dialog could not open. See the add-in log.";
            return Result.Failed;
        }
    }
}
