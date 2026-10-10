using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class UndoRestorePolicyTests
{
    [Test]
    [Arguments(true, false, false, null, false, false)]
    [Arguments(true, false, true, null, false, true)]
    [Arguments(true, false, false, null, true, true)]
    [Arguments(false, false, false, null, false, true)]
    [Arguments(false, true, false, null, false, false)]
    [Arguments(false, false, false, "", false, true)]
    [Arguments(false, false, false, "MCP: Move", false, false)]
    [Arguments(true, false, true, "MCP: Move", false, false)]
    [Arguments(true, false, false, "MCP: Move", true, false)]
    public async Task ShouldRestore_FollowsTruthTable(bool success, bool partial, bool rolledBack, string? undoName,
        bool needsConfirmation, bool expected) =>
        await Assert.That(UndoRestorePolicy.ShouldRestore(success, partial, rolledBack, undoName, needsConfirmation))
            .IsEqualTo(expected);

    [Test]
    public async Task ShouldRestore_UndoNeutralSuccessWithoutEntry_Restores()
    {
        await Assert.That(UndoRestorePolicy.ShouldRestore(true, false, false, null, false, true)).IsTrue();
        await Assert.That(UndoRestorePolicy.ShouldRestore(true, false, false, "MCP: Override", false, true)).IsFalse();
    }
}
