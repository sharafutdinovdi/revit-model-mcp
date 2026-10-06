using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ResponseBudgetTests
{
    private static StoredResponse[] Entries() =>
    [
        new("newest", DateTimeOffset.UnixEpoch.AddSeconds(2), 30),
        new("oldest", DateTimeOffset.UnixEpoch, 10),
        new("middle", DateTimeOffset.UnixEpoch.AddSeconds(1), 20)
    ];

    [Test]
    public async Task CountEvictsOldestFirst()
    {
        await Assert.That(string.Join(",", ResponseBudget.SelectEvictions(Entries(), 1, 100))).IsEqualTo("oldest,middle");
    }

    [Test]
    public async Task BytesEvictOldestFirst()
    {
        await Assert.That(string.Join(",", ResponseBudget.SelectEvictions(Entries(), 10, 50))).IsEqualTo("oldest");
    }

    [Test]
    public async Task BothLimitsApply()
    {
        await Assert.That(string.Join(",", ResponseBudget.SelectEvictions(Entries(), 2, 30))).IsEqualTo("oldest,middle");
    }

    [Test]
    public async Task NewestIsKeptEvenOverBudget()
    {
        await Assert.That(string.Join(",", ResponseBudget.SelectEvictions(Entries(), 1, 1))).IsEqualTo("oldest,middle");
        await Assert.That(ResponseBudget.SelectEvictions([Entries()[0]], 1, 1).Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyAndUnderBudgetNeedNoEvictions()
    {
        await Assert.That(ResponseBudget.SelectEvictions([], 1, 1).Count).IsEqualTo(0);
        await Assert.That(ResponseBudget.SelectEvictions(Entries(), 3, 60).Count).IsEqualTo(0);
    }

    [Test]
    public async Task TiesPreserveInputOrder()
    {
        StoredResponse[] entries = [new("first", DateTimeOffset.UnixEpoch, 1), new("second", DateTimeOffset.UnixEpoch, 1), new("last", DateTimeOffset.UnixEpoch, 1)];
        await Assert.That(string.Join(",", ResponseBudget.SelectEvictions(entries, 1, 100))).IsEqualTo("first,second");
    }
}
