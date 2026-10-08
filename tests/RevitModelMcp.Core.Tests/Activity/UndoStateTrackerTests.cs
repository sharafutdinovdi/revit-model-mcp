using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class UndoStateTrackerTests
{
    [Test]
    public async Task RecordCommit_StoresDocumentAndName()
    {
        var tracker = new UndoStateTracker();
        tracker.RecordCommit("Model", "MCP (mcp): Move");
        await Assert.That(tracker.Snapshot()).IsEqualTo(new UndoState("Model", "MCP (mcp): Move"));
    }

    [Test]
    public async Task RecordEntryRemoved_ClearsNameAndKeepsDocument()
    {
        var tracker = new UndoStateTracker();
        tracker.RecordCommit("Model", "MCP (mcp): Move");
        tracker.RecordEntryRemoved("Model");
        await Assert.That(tracker.Snapshot()).IsEqualTo(new UndoState("Model", null));
    }

    [Test]
    public async Task TemporaryScope_DiscardsCommitAndRollbackEvents()
    {
        var tracker = new UndoStateTracker();
        tracker.RecordCommit("Model", "MCP (mcp): Move");
        using (tracker.BeginTemporary())
        {
            tracker.RecordCommit("Model", "Prepare element snapshot");
            tracker.RecordEntryRemoved("Model");
        }
        await Assert.That(tracker.Snapshot()).IsEqualTo(new UndoState("Model", "MCP (mcp): Move"));
    }

    [Test]
    public async Task WithoutTemporaryScope_RollbackRefusesTheNextUndo()
    {
        var tracker = new UndoStateTracker();
        tracker.RecordCommit("Model", "MCP (mcp): Move");
        tracker.RecordEntryRemoved("Model");
        await Assert.That(UndoEligibility.IsAllowed(true, false, "MCP (mcp): Move", tracker.Snapshot().LastTransactionName, out _)).IsFalse();
    }

    [Test]
    public async Task TemporaryScope_KeepsUndoEligibleForTheLastAction()
    {
        var tracker = new UndoStateTracker();
        tracker.RecordCommit("Model", "MCP (mcp): Move");
        using (tracker.BeginTemporary()) tracker.RecordEntryRemoved("Model");
        await Assert.That(UndoEligibility.IsAllowed(true, false, "MCP (mcp): Move", tracker.Snapshot().LastTransactionName, out var reason)).IsTrue();
        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task TemporaryScope_DisposeTwiceRestoresOnlyOnce()
    {
        var tracker = new UndoStateTracker();
        tracker.RecordCommit("Model", "A");
        var scope = tracker.BeginTemporary();
        scope.Dispose();
        tracker.RecordCommit("Model", "B");
        scope.Dispose();
        await Assert.That(tracker.Snapshot().LastTransactionName).IsEqualTo("B");
    }

    [Test]
    public async Task Reset_ClearsState()
    {
        var tracker = new UndoStateTracker();
        tracker.RecordCommit("Model", "A");
        tracker.Reset();
        await Assert.That(tracker.Snapshot()).IsEqualTo(default(UndoState));
    }
}
