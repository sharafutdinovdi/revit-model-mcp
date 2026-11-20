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
        await Assert.That(TaggingPolicy.GetPlainPathReason(count, false, true, null)).IsNull();
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
        await Assert.That(TaggingPolicy.GetPlainPathReason(
            count, includesRooms, supportsTemporaryVisibility, hasActiveTemporaryMode ? "TemporaryHideIsolate" : null)).IsNotNull();
    }

    [Test]
    [Arguments(0, true, true, null, "rooms-in-call")]
    [Arguments(0, false, true, null, "no-independent-targets")]
    [Arguments(20, false, false, null, "view-cannot-enter-mode")]
    [Arguments(20, false, true, "TemporaryHideIsolate", "temporary-mode:TemporaryHideIsolate")]
    [Arguments(20, false, true, "RevealHiddenElements,TemporaryViewProperties", "temporary-mode:RevealHiddenElements,TemporaryViewProperties")]
    public async Task PlainPathReportsItsReason(int count, bool includesRooms, bool supportsTemporaryVisibility,
        string? activeTemporaryModes, string reason)
    {
        await Assert.That(TaggingPolicy.GetPlainPathReason(count, includesRooms, supportsTemporaryVisibility, activeTemporaryModes))
            .IsEqualTo(reason);
    }

    [Test]
    [Arguments(3, null, null, "Tagging path=isolated-batches; batches=3; batchSize=20; reason=none; exception=none.")]
    [Arguments(0, "rooms-in-call", null, "Tagging path=plain; batches=0; batchSize=20; reason=rooms-in-call; exception=none.")]
    [Arguments(0, null, null, "Tagging path=plain; batches=0; batchSize=20; reason=no-batches-started; exception=none.")]
    [Arguments(2, "isolation-refused-at-batch-3", "InvalidOperationException",
        "Tagging path=plain; batches=2; batchSize=20; reason=isolation-refused-at-batch-3; exception=InvalidOperationException.")]
    public async Task PathLogContainsCountsAndFixedDiagnostics(int batches, string? reason, string? exceptionType, string expected)
    {
        await Assert.That(TaggingPolicy.BuildPathLog(batches, reason, exceptionType)).IsEqualTo(expected);
    }
}
