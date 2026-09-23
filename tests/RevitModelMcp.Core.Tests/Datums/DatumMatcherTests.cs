using RevitModelMcp.Core.Datums;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Datums;

public sealed class DatumMatcherTests
{
    private static readonly DatumTransform Identity = DatumTransform.Identity;
    private static DatumMatchOptions Options(params string[] kinds) => new(kinds, new Dictionary<string, string>());
    private static DatumRecord Line(long id, string name, double y = 0, bool reverse = false) =>
        new(id, name, "grid", Start: reverse ? new(10, y) : new(0, y),
            End: reverse ? new(0, y) : new(10, y));

    [Test]
    public async Task ExactAndOffsetMatch()
    {
        var aligned = DatumMatcher.Match([Line(1, "A")], [Line(2, "A")], Identity, Options("grids"));
        await Assert.That(aligned[0].Status).IsEqualTo("aligned");
        var shifted = DatumMatcher.Match([Line(1, "A", 25 / 304.8)], [Line(2, "A")], Identity, Options("grids"));
        await Assert.That(shifted[0].Status).IsEqualTo("differs");
        await Assert.That(shifted[0].OffsetMm).IsEquivalentTo(new[] { 0.0, 25.0 });
    }

    [Test]
    public async Task RotatedAndReversedLines()
    {
        var quarterTurn = new DatumTransform(new(0, 0), new(0, 1), new(-1, 0), new(0, 0, 1));
        var rotated = DatumMatcher.Match([Line(1, "A")],
            [new DatumRecord(2, "A", "grid", Start: new(0, 0), End: new(0, 10))],
            quarterTurn, Options("grids"));
        await Assert.That(rotated[0].Status).IsEqualTo("aligned");
        var reversed = DatumMatcher.Match([Line(1, "A", reverse: true)], [Line(2, "A")],
            Identity, Options("grids"));
        await Assert.That(reversed[0].Reversed).IsTrue();
        await Assert.That(reversed[0].AngleDeg).IsEqualTo(0);
    }

    [Test]
    public async Task NameMappingAndGeometryReuse()
    {
        var mapped = DatumMatcher.Match([Line(1, "A")], [Line(2, "Host")], Identity,
            new DatumMatchOptions(["grids"], new Dictionary<string, string> { ["A"] = "Host" }, "P-", "-S"));
        await Assert.That(mapped[0].Name).IsEqualTo("Host");
        await Assert.That(mapped[0].MatchedBy).IsEqualTo("name");
        var prefixed = DatumMatcher.Match([Line(1, "A")], [Line(2, "P-A-S")], Identity,
            new DatumMatchOptions(["grids"], new Dictionary<string, string>(), "P-", "-S"));
        await Assert.That(prefixed[0].Name).IsEqualTo("P-A-S");
        var reused = DatumMatcher.Match([Line(1, "A")], [Line(2, "B")], Identity, Options("grids"));
        await Assert.That(reused[0].MatchedBy).IsEqualTo("geometry");
        await Assert.That(reused[0].NameDiffers).IsTrue();
        await Assert.That(reused.Count).IsEqualTo(1);
        var nameFirst = DatumMatcher.Match([Line(1, "A"), Line(3, "B")],
            [Line(2, "B"), Line(4, "C")], Identity, Options("grids"));
        await Assert.That(nameFirst[1].MatchedBy).IsEqualTo("name");
        await Assert.That(nameFirst[1].HostId).IsEqualTo(2);
    }

    [Test]
    public async Task LevelOffsetAndHostOnly()
    {
        var items = DatumMatcher.Match([new DatumRecord(1, "L", "level", 10)],
            [new DatumRecord(2, "L", "level", 10), new DatumRecord(3, "Unused", "level", 20)],
            Identity, new DatumMatchOptions(["levels"], new Dictionary<string, string>(), LevelOffsetMm: 150));
        await Assert.That(items[0].Status).IsEqualTo("differs");
        await Assert.That(items[0].DzMm).IsEqualTo(150);
        await Assert.That(items[1].Status).IsEqualTo("host_only");
    }

    [Test]
    public async Task UnsupportedCurvesAndTilt()
    {
        var arc = new DatumRecord(1, "A", "grid", Center: new(0, 0), Radius: 10);
        var differentRadius = DatumMatcher.Match([arc], [arc with { Id = 2, Radius = 9 }],
            Identity, Options("grids"));
        await Assert.That(differentRadius[0].Reason).IsEqualTo("radius differs");
        var differentShape = DatumMatcher.Match([arc], [Line(2, "A")], Identity, Options("grids"));
        await Assert.That(differentShape[0].Status).IsEqualTo("unsupported");
        var tilted = new DatumTransform(new(0, 0), new(1, 0), new(0, 1), new(0.1, 0, 0.995));
        await Assert.That(() => DatumMatcher.Match([new DatumRecord(1, "L", "level")], [], tilted,
            Options("levels"))).Throws<ArgumentException>();
        var grids = DatumMatcher.Match([Line(1, "A")], [Line(2, "A")], tilted, Options("grids"));
        await Assert.That(grids[0].Status).IsEqualTo("aligned");
    }
}
