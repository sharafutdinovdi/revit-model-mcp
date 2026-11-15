namespace RevitModelMcp.Core.Control;

public static class ControlExecutionPolicy
{
    // Scripts and batches can switch views or documents through the Revit API.
    public static bool CanExecuteFromIdling(string command) => command is not
        ("activate-view" or "activate-document" or "open-document" or "new-document" or
         "close-document" or "close-views" or "show" or "execute-code" or "run-tool" or
         "batch" or "process-models");

    public const string ViewChangeDeferredMessage =
        "Revit refused the view change for now. Repeat the call when Revit is ready.";
}
