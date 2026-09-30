using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchDeadlinePolicyTests
{
    [Test]
    public async Task Decide_UsesSuppliedClockAndWorkerFacts()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var limit = TimeSpan.FromSeconds(60);
        await Assert.That(BatchDeadlinePolicy.Decide(false, false, now, now.AddSeconds(1), now, limit))
            .IsEqualTo(BatchDeadlineDecision.Continue);
        await Assert.That(BatchDeadlinePolicy.Decide(false, true, now, now.AddSeconds(1), now, limit))
            .IsEqualTo(BatchDeadlineDecision.FailAndRecycle);
        await Assert.That(BatchDeadlinePolicy.Decide(false, false, now, now, now, limit))
            .IsEqualTo(BatchDeadlineDecision.FailAndRecycle);
        await Assert.That(BatchDeadlinePolicy.Decide(false, false, now, now.AddSeconds(1), now.AddSeconds(-61), limit))
            .IsEqualTo(BatchDeadlineDecision.FailAndRecycle);
        await Assert.That(BatchDeadlinePolicy.Decide(true, true, now, now, now, limit))
            .IsEqualTo(BatchDeadlineDecision.Cancelled);
    }
}
