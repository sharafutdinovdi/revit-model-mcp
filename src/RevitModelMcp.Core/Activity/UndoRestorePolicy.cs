namespace RevitModelMcp.Core.Activity;

/// <summary>Decides whether a finished job must put the undo tracking back to its state before the job.</summary>
public static class UndoRestorePolicy
{
    /// <summary>
    /// True when the job left no undo entry behind of its own and either rolled back, only previewed a
    /// confirmation, or failed outright. Failures that still committed (a named undo entry exists) are not restored.
    /// </summary>
    public static bool ShouldRestore(bool success, bool partial, bool rolledBack, string? undoName, bool needsConfirmation) =>
        string.IsNullOrEmpty(undoName) && (rolledBack || needsConfirmation || (!success && !partial));
}
