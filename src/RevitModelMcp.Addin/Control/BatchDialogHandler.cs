using Autodesk.Revit.UI.Events;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Control;

internal sealed class BatchDialogHandler
{
    public bool UnknownDialogSeen { get; private set; }

    public void OnDialog(object? sender, DialogBoxShowingEventArgs arguments)
    {
        var decision = BatchDialogPolicy.Decide(arguments.DialogId, arguments.GetType().Name);
        if (decision.Allowed && decision.OverrideResult.HasValue)
            arguments.OverrideResult(decision.OverrideResult.Value);
        else UnknownDialogSeen = true;
    }
}
