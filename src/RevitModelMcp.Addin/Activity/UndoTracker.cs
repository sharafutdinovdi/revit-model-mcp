using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Activity;

/// <summary>
/// Tracks the name Revit's undo stack reports for the active document, from <c>DocumentChanged</c>.
/// Feeds <c>revit_undo_last</c> eligibility and marks activity entries undone when their transaction
/// or transaction group leaves the top of the stack through undo or rollback.
/// </summary>
internal static class UndoTracker
{
    private static readonly UndoStateTracker State = new();

    public static void OnDocumentChanged(DocumentChangedEventArgs args)
    {
        var document = args.GetDocument();
        var names = args.GetTransactionNames();
        var name = names.Count > 0 ? names[^1] : null;
        switch (args.Operation)
        {
            case UndoOperation.TransactionCommitted:
            case UndoOperation.TransactionRedone:
                State.RecordCommit(document.Title, name);
                break;
            case UndoOperation.TransactionUndone:
            case UndoOperation.TransactionRolledBack:
            case UndoOperation.TransactionGroupRolledBack:
                State.RecordEntryRemoved(document.Title);
                if (name is not null) ActivityLog.MarkUndoneByEntryName(name);
                break;
        }
    }

    public static (string? DocumentTitle, string? LastTransactionName) Snapshot()
    {
        var state = State.Snapshot();
        return (state.DocumentTitle, state.LastTransactionName);
    }

    /// <summary>
    /// Overrides the tracked document/name right after this session's own commit or transaction-group
    /// assimilate succeeds. <c>DocumentChanged</c> reports the name the underlying Transaction had at
    /// commit time, never the descriptive name a TransactionGroup is renamed to just before Assimilate;
    /// calling this immediately afterward keeps <c>revit_undo_last</c> comparing against the exact name
    /// recorded in the activity log and returned to the client as <c>undoName</c>.
    /// </summary>
    public static void RecordOwnCommit(string documentTitle, string undoName) =>
        State.RecordCommit(documentTitle, undoName);

    /// <summary>Restores a <see cref="Snapshot"/> after a job rolled back everything it committed.</summary>
    public static void Restore((string? DocumentTitle, string? LastTransactionName) state) =>
        State.Restore(new UndoState(state.DocumentTitle, state.LastTransactionName));

    /// <summary>
    /// Wraps a temporary transaction or group that is always rolled back (element captures, export probes),
    /// so its commit and rollback events never change which entry <c>revit_undo_last</c> may undo.
    /// Dispose after the rollback.
    /// </summary>
    public static IDisposable BeginTemporary() => State.BeginTemporary();

    public static void Reset() => State.Reset();
}
