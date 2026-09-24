using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace RevitModelMcp.Activity;

/// <summary>
/// Tracks the name Revit's undo stack reports for the active document, from <c>DocumentChanged</c>.
/// Feeds <c>revit_undo_last</c> eligibility and marks activity entries undone when their transaction
/// or transaction group leaves the top of the stack through undo or rollback.
/// </summary>
internal static class UndoTracker
{
    private static readonly object SyncRoot = new();
    private static string? _documentTitle;
    private static string? _lastCommittedName;

    public static void OnDocumentChanged(DocumentChangedEventArgs args)
    {
        var document = args.GetDocument();
        var names = args.GetTransactionNames();
        var name = names.Count > 0 ? names[^1] : null;
        lock (SyncRoot)
        {
            switch (args.Operation)
            {
                case UndoOperation.TransactionCommitted:
                case UndoOperation.TransactionRedone:
                    _documentTitle = document.Title;
                    _lastCommittedName = name;
                    break;
                case UndoOperation.TransactionUndone:
                case UndoOperation.TransactionRolledBack:
                case UndoOperation.TransactionGroupRolledBack:
                    _documentTitle = document.Title;
                    // The undone/rolled-back entry no longer tops the stack; its exact predecessor is
                    // unknown from this event alone, so the next revit_undo_last call is conservatively
                    // refused until another commit is observed.
                    _lastCommittedName = null;
                    if (name is not null) ActivityLog.MarkUndoneByEntryName(name);
                    break;
            }
        }
    }

    public static (string? DocumentTitle, string? LastTransactionName) Snapshot()
    {
        lock (SyncRoot) return (_documentTitle, _lastCommittedName);
    }

    public static void Reset()
    {
        lock (SyncRoot)
        {
            _documentTitle = null;
            _lastCommittedName = null;
        }
    }
}
