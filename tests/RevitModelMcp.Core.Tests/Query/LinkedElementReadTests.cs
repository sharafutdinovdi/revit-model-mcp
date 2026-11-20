using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Core.Tests.Query;

public sealed class LinkedElementReadTests
{
    [Test]
    public async Task Selector_DistinguishesHostAllIdsAndExactNames()
    {
        await Assert.That(LinkSelector.Parse(null).IsHost).IsTrue();
        await Assert.That(LinkSelector.Parse(" * ").Matches(42, "MEP: 1", "MEP.rvt")).IsTrue();
        await Assert.That(LinkSelector.Parse("42").Matches(42, "MEP: 1", "MEP.rvt")).IsTrue();
        await Assert.That(LinkSelector.Parse("42").Matches(43, "MEP: 1", "MEP.rvt")).IsFalse();
        await Assert.That(LinkSelector.Parse(" mep.rvt ").Matches(42, "MEP: 1", "MEP.rvt")).IsTrue();
        await Assert.That(LinkSelector.Parse("MEP").Matches(42, "MEP: 1", "MEP.rvt")).IsFalse();
        foreach (var invalid in new[] { "", " ", "0", "-1" })
            Assert.Throws<ArgumentException>(() => LinkSelector.Parse(invalid));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Transform_ConvertsPointsCurvesRoomsAndAllBoxCorners(bool rotate)
    {
        var transform = new LinkReadTransform([100, 200, 300],
            rotate ? [0, 1, 0] : [1, 0, 0], rotate ? [-1, 0, 0] : [0, 1, 0], [0, 0, 1]);
        var row = new ElementDetailsData
        {
            Location = new ElementLocationData { Type = "point", XMm = 1, YMm = 2, ZMm = 3, StartMm = [1, 2, 3], EndMm = [4, 5, 6], LengthMm = 10 },
            RoomCenterMm = [1, 2, 3],
            BoundingBox = new ElementBoundingBoxData { MinMm = [0, 0, 0], MaxMm = [10, 20, 30], CenterMm = [5, 10, 15] },
            Room = new RoomDetails
            {
                Level = "Link Level",
                Boundaries = [new RoomBoundaryLoop { Segments = [new RoomBoundarySegment { StartMm = new VectorSnapshot { X = 1, Y = 2, Z = 3 }, EndMm = new VectorSnapshot { X = 4, Y = 5, Z = 6 } }] }]
            }
        };
        transform.Apply(row, 42, "MEP");
        await Assert.That(row.LinkInstanceId).IsEqualTo(42L);
        await Assert.That(row.LinkName).IsEqualTo("MEP");
        await Assert.That(row.Location.XMm).IsEqualTo(rotate ? 98d : 101d);
        await Assert.That(row.Location.YMm).IsEqualTo(rotate ? 201d : 202d);
        await Assert.That(row.Location.ZMm).IsEqualTo(303d);
        await Assert.That(row.Location.LengthMm).IsEqualTo(10d);
        await Assert.That(row.Location.StartMm![0]).IsEqualTo(rotate ? 98d : 101d);
        await Assert.That(row.Location.EndMm![0]).IsEqualTo(rotate ? 95d : 104d);
        await Assert.That(row.RoomCenterMm![0]).IsEqualTo(rotate ? 98d : 101d);
        await Assert.That(row.BoundingBox.MinMm[0]).IsEqualTo(rotate ? 80d : 100d);
        await Assert.That(row.BoundingBox.MaxMm[0]).IsEqualTo(rotate ? 100d : 110d);
        await Assert.That(row.BoundingBox.MinMm[1]).IsEqualTo(200d);
        await Assert.That(row.BoundingBox.MaxMm[1]).IsEqualTo(rotate ? 210d : 220d);
        await Assert.That(row.BoundingBox.CenterMm[2]).IsEqualTo(315d);
        await Assert.That(row.Room.Boundaries[0].Segments[0].StartMm.X).IsEqualTo(rotate ? 98d : 101d);
        await Assert.That(row.Room.Level).IsEqualTo("Link Level");
    }

    [Test]
    public async Task Transform_ShapesRowsWithoutGeometry()
    {
        var row = new QueryElementItem { Id = 7 };
        new LinkReadTransform([0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]).Apply(row, 42, "MEP");
        await Assert.That(row.LinkInstanceId).IsEqualTo(42L);
        await Assert.That(row.Location).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Paging_UsesLinkInstanceToBreakEqualElementIdsDeterministically(bool descending)
    {
        var records = new[]
        {
            new PreparedQueryRecord { Id = 2, LinkInstanceId = 20 },
            new PreparedQueryRecord { Id = 1, LinkInstanceId = 20 },
            new PreparedQueryRecord { Id = 1, LinkInstanceId = 10 },
            new PreparedQueryRecord { Id = 2, LinkInstanceId = 10 }
        };
        foreach (var field in new[] { "id", "level" })
        {
            var sort = new QuerySortSpec { Field = field, Descending = descending };
            var page = QueryResultProcessor.SortAndPage(records, sort, 1, 2, out var hasMore);
            var repeated = QueryResultProcessor.SortAndPage(records.AsEnumerable().Reverse().ToList(), sort, 1, 2, out _);
            await Assert.That(hasMore).IsTrue();
            await Assert.That(page[0].LinkInstanceId).IsEqualTo(20L);
            await Assert.That(page[1].LinkInstanceId).IsEqualTo(10L);
            await Assert.That(page[0]).IsEqualTo(repeated[0]);
            var last = QueryResultProcessor.SortAndPage(records, sort, 3, 2, out hasMore);
            await Assert.That(last.Count).IsEqualTo(1);
            await Assert.That(hasMore).IsFalse();
        }
    }

    [Test]
    public async Task PublicJobParser_AcceptsHostAndLinkedUniqueIdLookupsAndRejectsInvalidSelectors()
    {
        foreach (var command in new[] { "query-elements", "element-details" })
        {
            var parsed = ControlJobParser.Parse($$"""{"command":"{{command}}","link":"*","uniqueId":"external-id"}""");
            await Assert.That(parsed.Error).IsNull();
            await Assert.That(parsed.Link).IsEqualTo("*");
            await Assert.That(parsed.UniqueId).IsEqualTo("external-id");
            var invalid = ControlJobParser.Parse($$"""{"command":"{{command}}","link":"0","id":7}""");
            await Assert.That(invalid.Kind).IsEqualTo(ControlJobKind.Invalid);
        }
        await Assert.That(ControlJobParser.Parse("""{"command":"element-details","uniqueId":"external-id"}""").Error).IsNull();
        await Assert.That(ControlJobParser.Parse("""{"command":"element-details","id":7,"uniqueId":"external-id"}""").Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"element-details"}""").Kind).IsEqualTo(ControlJobKind.Invalid);
    }
}
