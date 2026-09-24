using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class UndoEligibilityTests
{
    [Test]
    public async Task IsAllowed_MatchingNamesActiveNoPending_ReturnsTrue()
    {
        var allowed = UndoEligibility.IsAllowed(
            isActiveDocument: true, hasPendingCommand: false,
            recordedUndoName: "MCP (claude-code): Moved 3 elements in Project1.rvt",
            lastTransactionName: "MCP (claude-code): Moved 3 elements in Project1.rvt",
            out var reason);
        await Assert.That(allowed).IsTrue();
        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task IsAllowed_NotActiveDocument_ReturnsFalse()
    {
        var allowed = UndoEligibility.IsAllowed(
            isActiveDocument: false, hasPendingCommand: false,
            recordedUndoName: "MCP (a): x", lastTransactionName: "MCP (a): x", out var reason);
        await Assert.That(allowed).IsFalse();
        await Assert.That(reason).IsEqualTo("the target document is not the active document");
    }

    [Test]
    public async Task IsAllowed_PendingCommand_ReturnsFalse()
    {
        var allowed = UndoEligibility.IsAllowed(
            isActiveDocument: true, hasPendingCommand: true,
            recordedUndoName: "MCP (a): x", lastTransactionName: "MCP (a): x", out var reason);
        await Assert.That(allowed).IsFalse();
        await Assert.That(reason).IsEqualTo("a command is currently pending in Revit");
    }

    [Test]
    public async Task IsAllowed_NoRecordedAction_ReturnsFalse()
    {
        var allowed = UndoEligibility.IsAllowed(
            isActiveDocument: true, hasPendingCommand: false,
            recordedUndoName: null, lastTransactionName: "Move Elements", out var reason);
        await Assert.That(allowed).IsFalse();
        await Assert.That(reason).IsEqualTo("there is no recorded MCP action to undo");
    }

    [Test]
    public async Task IsAllowed_LastTransactionIsNotOurs_ReturnsFalseWithName()
    {
        var allowed = UndoEligibility.IsAllowed(
            isActiveDocument: true, hasPendingCommand: false,
            recordedUndoName: "MCP (claude-code): Moved 3 elements in Project1.rvt",
            lastTransactionName: "Move Elements", out var reason);
        await Assert.That(allowed).IsFalse();
        await Assert.That(reason).IsEqualTo("the last change in Revit is not ours: Move Elements");
    }

    [Test]
    public async Task IsAllowed_LastTransactionUnknown_ReportsNoneInReason()
    {
        var allowed = UndoEligibility.IsAllowed(
            isActiveDocument: true, hasPendingCommand: false,
            recordedUndoName: "MCP (claude-code): Moved 3 elements in Project1.rvt",
            lastTransactionName: null, out var reason);
        await Assert.That(allowed).IsFalse();
        await Assert.That(reason).IsEqualTo("the last change in Revit is not ours: (none)");
    }
}
