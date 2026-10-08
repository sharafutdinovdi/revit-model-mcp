namespace RevitModelMcp.Core.Activity;

/// <summary>Document title and top undo entry name, as seen by <c>revit_undo_last</c>.</summary>
public readonly record struct UndoState(string? DocumentTitle, string? LastTransactionName);

/// <summary>
/// Thread-safe record of the undo entry Revit reports on top of its stack. Temporary transactions that this
/// session opens and rolls back for read-only work are wrapped in <see cref="BeginTemporary"/>, so the
/// rollback leaves the recorded state exactly as it was.
/// </summary>
public sealed class UndoStateTracker
{
    private readonly object _syncRoot = new();
    private UndoState _state;

    public UndoState Snapshot()
    {
        lock (_syncRoot) return _state;
    }

    public void RecordCommit(string documentTitle, string? name)
    {
        lock (_syncRoot) _state = new UndoState(documentTitle, name);
    }

    /// <summary>
    /// Records that an entry left the top of the stack. Its exact predecessor is unknown from the event alone,
    /// so the next undo is refused until another commit is observed.
    /// </summary>
    public void RecordEntryRemoved(string documentTitle)
    {
        lock (_syncRoot) _state = new UndoState(documentTitle, null);
    }

    public void Restore(UndoState state)
    {
        lock (_syncRoot) _state = state;
    }

    public void Reset()
    {
        lock (_syncRoot) _state = default;
    }

    /// <summary>Restores the state captured now when disposed, discarding every event seen in between.</summary>
    public IDisposable BeginTemporary() => new TemporaryScope(this, Snapshot());

    private sealed class TemporaryScope(UndoStateTracker owner, UndoState state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Restore(state);
        }
    }
}
