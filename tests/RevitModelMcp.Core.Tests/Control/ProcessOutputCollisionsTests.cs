using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ProcessOutputCollisionsTests
{
    [Test]
    public async Task Find_DistinctNames_ReturnsNoGroups()
    {
        var groups = ProcessOutputCollisions.Find([@"A\x.rvt", @"A\y.rvt"]);

        await Assert.That(groups.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Find_SameNameInTwoFolders_ReturnsOneGroup()
    {
        var groups = ProcessOutputCollisions.Find([@"A\x.rvt", @"B\x.rvt", @"A\y.rvt"]);

        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].Count).IsEqualTo(2);
        await Assert.That(groups[0][0]).IsEqualTo(@"A\x.rvt");
        await Assert.That(groups[0][1]).IsEqualTo(@"B\x.rvt");
    }

    [Test]
    public async Task Find_IsCaseInsensitive()
    {
        var groups = ProcessOutputCollisions.Find([@"A\X.rvt", @"B\x.RVT"]);

        await Assert.That(groups.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Find_HandlesForwardSlashes()
    {
        var groups = ProcessOutputCollisions.Find(["A/x.rvt", @"B\x.rvt"]);

        await Assert.That(groups.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Find_DifferentExtensionsSameStem_Collide()
    {
        var groups = ProcessOutputCollisions.Find([@"A\x.rvt", @"B\x.rfa"]);

        await Assert.That(groups.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FormatError_ListsNameAndPaths()
    {
        var groups = ProcessOutputCollisions.Find([@"A\x.rvt", @"B\x.rvt"]);

        var message = ProcessOutputCollisions.FormatError(groups);

        await Assert.That(message).Contains("\"x\"");
        await Assert.That(message).Contains(@"A\x.rvt");
        await Assert.That(message).Contains(@"B\x.rvt");
        await Assert.That(message).Contains("separate calls");
    }

    [Test]
    public async Task FormatError_TruncatesAfterFiveGroups()
    {
        var sources = Enumerable.Range(1, 7).SelectMany(i => new[] { $@"A\m{i}.rvt", $@"B\m{i}.rvt" });
        var groups = ProcessOutputCollisions.Find(sources);

        var message = ProcessOutputCollisions.FormatError(groups);

        await Assert.That(groups.Count).IsEqualTo(7);
        await Assert.That(message).Contains("\"m5\"");
        await Assert.That(message).DoesNotContain("\"m6\"");
        await Assert.That(message).Contains("and 2 more");
    }
}
