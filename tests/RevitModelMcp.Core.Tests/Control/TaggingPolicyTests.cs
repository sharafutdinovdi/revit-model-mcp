using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class TaggingPolicyTests
{
    [Test]
    [Arguments(0, 0, 0)]
    [Arguments(1, 1, 1)]
    [Arguments(20, 1, 20)]
    [Arguments(21, 2, 1)]
    [Arguments(40, 2, 20)]
    [Arguments(41, 3, 1)]
    [Arguments(500, 25, 20)]
    public async Task IsolationBatchesPreserveOrderedTargetsAndPartialLastBatch(int count, int batchCount, int lastSize)
    {
        var targets = Enumerable.Range(0, count).Reverse().ToArray();
        var batches = TaggingPolicy.PlanIsolationBatches(targets).ToArray();
        await Assert.That(batches.Length).IsEqualTo(batchCount);
        await Assert.That(batches.LastOrDefault().Count).IsEqualTo(lastSize);
        await Assert.That(batches.SelectMany(batch => batch).SequenceEqual(targets)).IsTrue();
        await Assert.That(batches.All(batch => batch.Count is > 0 and <= TaggingPolicy.IsolationBatchSize)).IsTrue();
        await Assert.That(batches.All(batch => ReferenceEquals(batch.Array, targets))).IsTrue();
    }

    [Test]
    public async Task NullIsolationTargetsAreRejected()
    {
        await Assert.That(() => TaggingPolicy.PlanIsolationBatches<int>(null!).ToArray())
            .Throws<ArgumentNullException>();
    }

    [Test]
    [Arguments(1)]
    [Arguments(500)]
    public async Task IndependentTargetsUseIsolationWhenTheViewIsUnmodified(int count)
    {
        await Assert.That(TaggingPolicy.CanTemporarilyIsolate(count, false, true, false)).IsTrue();
    }

    [Test]
    [Arguments(0, false, true, false)]
    [Arguments(20, true, true, false)]
    [Arguments(20, false, false, false)]
    [Arguments(20, false, true, true)]
    [Arguments(20, true, false, true)]
    public async Task EmptyRoomUnsupportedOrAlreadyTemporaryViewsUseThePlainPath(
        int count, bool includesRooms, bool supportsTemporaryVisibility, bool hasActiveTemporaryMode)
    {
        await Assert.That(TaggingPolicy.CanTemporarilyIsolate(
            count, includesRooms, supportsTemporaryVisibility, hasActiveTemporaryMode)).IsFalse();
    }
}
