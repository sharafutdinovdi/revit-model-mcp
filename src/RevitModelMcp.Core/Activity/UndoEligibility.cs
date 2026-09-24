namespace RevitModelMcp.Core.Activity;

/// <summary>
/// Pure eligibility check for <c>revit_undo_last</c>: allowed only when the target document is active,
/// no command is pending, and the name Revit reports for its last undo entry matches the name recorded
/// for the newest activity entry.
/// </summary>
public static class UndoEligibility
{
    public static bool IsAllowed(
        bool isActiveDocument,
        bool hasPendingCommand,
        string? recordedUndoName,
        string? lastTransactionName,
        out string? reason)
    {
        if (!isActiveDocument)
        {
            reason = "the target document is not the active document";
            return false;
        }
        if (hasPendingCommand)
        {
            reason = "a command is currently pending in Revit";
            return false;
        }
        if (string.IsNullOrEmpty(recordedUndoName))
        {
            reason = "there is no recorded MCP action to undo";
            return false;
        }
        if (!string.Equals(recordedUndoName, lastTransactionName, StringComparison.Ordinal))
        {
            reason = $"the last change in Revit is not ours: {(string.IsNullOrEmpty(lastTransactionName) ? "(none)" : lastTransactionName)}";
            return false;
        }
        reason = null;
        return true;
    }
}
