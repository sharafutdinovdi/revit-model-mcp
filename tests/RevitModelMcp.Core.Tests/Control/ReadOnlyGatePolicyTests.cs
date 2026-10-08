using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ReadOnlyGatePolicyTests
{
    [Test]
    public async Task RefusesSubmission_ActionsAndBatchSupervisorStart()
    {
        foreach (var command in new[] { "select", "move", "undo-last", "export", "batch-supervisor-start" })
            await Assert.That(ReadOnlyGatePolicy.RefusesSubmission(command)).IsTrue();
    }

    [Test]
    public async Task RefusesSubmission_ReadsAndWorkerCommandsPass()
    {
        foreach (var command in new[] { "ping", "jobs", "batch-prepass", "batch-open", "batch-snapshot", "batch-close", "capture-elements", null })
            await Assert.That(ReadOnlyGatePolicy.RefusesSubmission(command)).IsFalse();
    }

    [Test]
    public async Task Message_MatchesServerRefusalText()
    {
        await Assert.That(ReadOnlyGatePolicy.Message).IsEqualTo("read-only mode");
    }
}
