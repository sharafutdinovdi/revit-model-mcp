using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class NoChangeGuardPolicyTests
{
    [Test]
    [Arguments("select")]
    [Arguments("show")]
    [Arguments("isolate")]
    [Arguments("override-graphics")]
    [Arguments("activate-view")]
    [Arguments("close-views")]
    [Arguments("set-view-visibility")]
    [Arguments("open-document")]
    [Arguments("activate-document")]
    [Arguments("close-document")]
    [Arguments("undo-last")]
    public async Task UiOnlyCommandsAreNeverChecked(string command)
    {
        await Assert.That(NoChangeGuardPolicy.IsUiOnly(command)).IsTrue();
        await Assert.That(NoChangeGuardPolicy.Applies(command, false, true)).IsFalse();
    }

    [Test]
    [Arguments("move")]
    [Arguments("set-parameter")]
    [Arguments("delete")]
    [Arguments("tag-elements")]
    [Arguments("create-schedule")]
    public async Task ModelChangingCommandsAreCheckedWhenGrouped(string command)
    {
        await Assert.That(NoChangeGuardPolicy.Applies(command, false, true)).IsTrue();
        await Assert.That(NoChangeGuardPolicy.Applies(command, true, true)).IsFalse();
        await Assert.That(NoChangeGuardPolicy.Applies(command, false, false)).IsFalse();
    }

    [Test]
    [Arguments("select", 0)]
    [Arguments("isolate", 0)]
    [Arguments("isolate", 3)]
    [Arguments("override-graphics", 0)]
    [Arguments("set-view-visibility", 0)]
    public async Task UiOnlyStepsWithoutChangesKeepNoUndoEntry(string command, int changed)
    {
        await Assert.That(NoChangeGuardPolicy.KeepsUndoEntry(command, changed)).IsFalse();
    }

    [Test]
    [Arguments("override-graphics")]
    [Arguments("set-view-visibility")]
    public async Task ViewOverridesThatChangedAnElementKeepTheUndoEntry(string command)
    {
        await Assert.That(NoChangeGuardPolicy.KeepsUndoEntry(command, 1)).IsTrue();
    }

    [Test]
    [Arguments("move", 0)]
    [Arguments("delete", 4)]
    public async Task ModelChangingCommandsKeepTheUndoEntry(string command, int changed)
    {
        await Assert.That(NoChangeGuardPolicy.KeepsUndoEntry(command, changed)).IsTrue();
    }

    [Test]
    [Arguments("isolate", true)]
    [Arguments("select", true)]
    [Arguments("show", true)]
    [Arguments("override-graphics", true)]
    [Arguments("set-view-visibility", true)]
    [Arguments("activate-view", true)]
    [Arguments("close-views", true)]
    [Arguments("undo-last", false)]
    [Arguments("open-document", false)]
    [Arguments("move", false)]
    public async Task UndoStateIsRestoredOnlyForUndoNeutralUiCommands(string command, bool expected) =>
        await Assert.That(NoChangeGuardPolicy.RestoresUndoStateWithoutEntry(command)).IsEqualTo(expected);
}
