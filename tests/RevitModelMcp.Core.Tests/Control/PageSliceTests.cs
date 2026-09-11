using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class PageSliceTests
{
    [Test]
    public async Task Create_MiddlePage_AppliesOffsetLimitAndHasMore()
    {
        var result = PageSlice.Create(new[] { 10, 11, 12, 13, 14 }, 2, 2);

        await Assert.That(result.Items).IsEquivalentTo(new[] { 12, 13 });
        await Assert.That(result.HasMore).IsTrue();
    }

    [Test]
    public async Task Create_LastPage_ReturnsRemainderWithoutMore()
    {
        var result = PageSlice.Create(new[] { 10, 11, 12, 13, 14 }, 4, 3);

        await Assert.That(result.Items).IsEquivalentTo(new[] { 14 });
        await Assert.That(result.HasMore).IsFalse();
    }

    [Test]
    public async Task Create_OffsetPastEnd_ReturnsEmptyPage()
    {
        var result = PageSlice.Create(new[] { 10, 11 }, 5, 10);

        await Assert.That(result.Items).IsEmpty();
        await Assert.That(result.HasMore).IsFalse();
    }
}
