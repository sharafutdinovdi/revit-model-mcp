using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class TaggingPolicyTests
{
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
