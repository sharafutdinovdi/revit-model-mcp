using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ReadOnlyGatePolicyTests
{
    [Test]
    public async Task RefusesSubmission_ActionsAndBatchSupervisorStart()
    {
        foreach (var command in new[] { "select", "move", "undo-last", "tag-elements", "create-schedule", "export", "batch-supervisor-start" })
            await Assert.That(ReadOnlyGatePolicy.RefusesSubmission(command)).IsTrue();
    }

    [Test]
    public async Task RefusesSubmission_ReadsAndWorkerCommandsPass()
    {
        foreach (var command in new[] { "set-read-only", "ping", "jobs", "batch-prepass", "batch-open", "batch-snapshot", "batch-close", "capture-elements", null })
            await Assert.That(ReadOnlyGatePolicy.RefusesSubmission(command)).IsFalse();
    }

    [Test]
    public async Task OnlyLockActionBypassesTheLock()
    {
        await Assert.That(ActionJobParser.IsAction("set-read-only")).IsTrue();
        await Assert.That(ReadOnlyGatePolicy.RefusesSubmission("set-read-only")).IsFalse();
        foreach (var command in new[] { "move", "delete", "select", "show", "save-document", "sync-document", "execute-code", "batch" })
            await Assert.That(ReadOnlyGatePolicy.RefusesSubmission(command)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task LockJobRequiresAnExplicitEnabledValue(bool enabled)
    {
        var parsed = ActionJobParser.Parse("set-read-only", new ControlJobContract { Enabled = enabled });
        await Assert.That(parsed.Error).IsNull();
        await Assert.That(parsed.Action!.Enabled).IsEqualTo(enabled);
        var invalid = ActionJobParser.Parse("set-read-only", new ControlJobContract());
        await Assert.That(invalid.Error).Contains("enabled is required");
    }
}
