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
}
