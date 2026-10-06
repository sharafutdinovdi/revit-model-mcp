using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchReadOnlyPolicyTests
{
    [Test]
    public async Task Allows_OnlySupervisorCommands()
    {
        foreach (var command in new[] { "ping", "jobs", "batch-prepass", "batch-open", "batch-snapshot", "batch-close" })
            await Assert.That(BatchReadOnlyPolicy.Allows(command)).IsTrue();
        foreach (var command in new[] { "model-snapshot", "save-document", "sync-document", "set-parameter", "batch", "open-document", "export-nwc", "undo-last", "select" })
            await Assert.That(BatchReadOnlyPolicy.Allows(command)).IsFalse();
    }

    [Test]
    public async Task RefusedInReadOnlyMode_OnlySupervisorStart()
    {
        await Assert.That(BatchReadOnlyPolicy.RefusedInReadOnlyMode("batch-supervisor-start")).IsTrue();
        foreach (var command in new[] { "ping", "jobs", "batch-prepass", "batch-open", "batch-snapshot", "batch-close", "batch", null })
            await Assert.That(BatchReadOnlyPolicy.RefusedInReadOnlyMode(command)).IsFalse();
    }

    [Test]
    public async Task Allows_ExactlyTheSixWorkerCommands()
    {
        foreach (var command in new[] { "ping", "jobs", "batch-prepass", "batch-open", "batch-snapshot", "batch-close" })
            await Assert.That(BatchReadOnlyPolicy.Allows(command)).IsTrue();
        foreach (var command in new[] { "batch-supervisor-start", "export", "execute-code" })
            await Assert.That(BatchReadOnlyPolicy.Allows(command)).IsFalse();
    }
}
