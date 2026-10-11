namespace RevitModelMcp.Core.Activity;

/// <summary>
/// Pure eligibility check for <c>revit_undo_last</c>: allowed only when the target document is active,
/// no command is pending, and the name Revit reports for its last undo entry matches the name recorded
/// for the newest undoable activity entry.
/// </summary>
public static class UndoEligibility
{
    public static ActivityEntry? NewestUndoable(IEnumerable<ActivityEntry> entries) =>
        entries.FirstOrDefault(entry => !entry.Undone && !string.IsNullOrEmpty(entry.UndoEntryName));

    /// <summary>Name of the transaction the isolate action commits; it leaves a Revit undo entry that is not an MCP undo target.</summary>
    public const string IsolateTransactionName = "revit_isolate";

    public const string IsolateRefusal =
        "The newest change in Revit is a temporary isolate (revit_isolate). Earlier MCP actions can no longer be undone with revit_undo_last; use Undo in Revit if you need to revert them.";

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
            reason = string.Equals(lastTransactionName, IsolateTransactionName, StringComparison.Ordinal)
                ? IsolateRefusal
                : string.IsNullOrEmpty(lastTransactionName)
                ? "Revit reports no undo entry that belongs to this MCP action; only the newest MCP action can be undone"
                : $"the last change in Revit is not ours: {lastTransactionName}";
            return false;
        }
        reason = null;
        return true;
    }
}
