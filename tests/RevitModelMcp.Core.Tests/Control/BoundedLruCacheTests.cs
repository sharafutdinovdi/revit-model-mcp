using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class BoundedLruCacheTests
{
    [Test]
    public async Task TryGet_Miss_ReturnsFalse()
    {
        var cache = new BoundedLruCache<string, int>(2);

        await Assert.That(cache.TryGet("a", out _)).IsFalse();
    }

    [Test]
    public async Task SetThenTryGet_ReturnsValue()
    {
        var cache = new BoundedLruCache<string, int>(2);
        cache.Set("a", 1);

        await Assert.That(cache.TryGet("a", out var value)).IsTrue();
        await Assert.That(value).IsEqualTo(1);
    }

    [Test]
    public async Task Set_OverCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new BoundedLruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3);

        await Assert.That(cache.Count).IsEqualTo(2);
        await Assert.That(cache.TryGet("a", out _)).IsFalse();
        await Assert.That(cache.TryGet("b", out _)).IsTrue();
        await Assert.That(cache.TryGet("c", out _)).IsTrue();
    }

    [Test]
    public async Task TryGet_RefreshesRecency()
    {
        var cache = new BoundedLruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.TryGet("a", out _);
        cache.Set("c", 3);

        await Assert.That(cache.TryGet("a", out _)).IsTrue();
        await Assert.That(cache.TryGet("b", out _)).IsFalse();
    }

    [Test]
    public async Task Set_Overwrite_DoesNotGrowCount()
    {
        var cache = new BoundedLruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("a", 2);

        await Assert.That(cache.Count).IsEqualTo(1);
        cache.TryGet("a", out var value);
        await Assert.That(value).IsEqualTo(2);
    }

    [Test]
    public async Task Constructor_CapacityBelowOne_Throws()
    {
        await Assert.That(() => new BoundedLruCache<string, int>(0)).Throws<ArgumentOutOfRangeException>();
    }
}
