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
}
