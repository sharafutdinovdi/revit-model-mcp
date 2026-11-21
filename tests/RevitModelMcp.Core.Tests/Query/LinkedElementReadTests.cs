using System.Text.Json;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Query;

public sealed class LinkedElementReadTests
{
    [Test]
    public async Task LinkedRows_SerializeLocalIdsWithoutHostIdFields()
    {
        var row = new QueryElementItem
        {
            Id = 7,
            Values = { ["ID"] = new QueryFieldValue { HasValue = true, Value = "7", NumericValue = 7 } }
        };
        using var hostJson = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<QueryElementsData>.Ok("query-elements", new QueryElementsData { Elements = [row] }, 1)));
        var hostRow = hostJson.RootElement.GetProperty("data").GetProperty("elements")[0];
        await Assert.That(hostRow.GetProperty("id").GetInt64()).IsEqualTo(7L);
        await Assert.That(hostRow.TryGetProperty("linkedElementId", out _)).IsFalse();
        var transform = new LinkReadTransform([0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]);
        transform.Apply(row, 42, "MEP");
        using var linkedJson = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<QueryElementsData>.Ok("query-elements", new QueryElementsData { Elements = [row] }, 1)));
        var linkedRow = linkedJson.RootElement.GetProperty("data").GetProperty("elements")[0];
        await Assert.That(linkedRow.TryGetProperty("id", out _)).IsFalse();
        await Assert.That(linkedRow.GetProperty("linkedElementId").GetInt64()).IsEqualTo(7L);
        await Assert.That(linkedRow.GetProperty("linkInstanceId").GetInt64()).IsEqualTo(42L);
        await Assert.That(linkedRow.GetProperty("values").TryGetProperty("id", out _)).IsFalse();
        await Assert.That(linkedRow.GetProperty("values").GetProperty("linkedElementId").GetProperty("numericValue").GetDouble()).IsEqualTo(7d);
    }

    [Test]
    public async Task LinkedDetails_SerializeLocalInstanceTypeAndBoundaryReferences()
    {
        var details = new ElementDetailsData
        {
            Element = new ViewElementDump { Id = 7 },
            TypeElement = new ElementTypeDetails { Id = 8 },
            Room = new RoomDetails
            {
                Boundaries = [new RoomBoundaryLoop { Segments = [new RoomBoundarySegment { ElementId = 9 }] }]
            }
        };
        using var hostJson = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<ElementDetailsData>.Ok("element-details", details, 1)));
        var host = hostJson.RootElement.GetProperty("data");
        await Assert.That(host.GetProperty("element").GetProperty("id").GetInt64()).IsEqualTo(7L);
        await Assert.That(host.GetProperty("typeElement").GetProperty("id").GetInt64()).IsEqualTo(8L);
        await Assert.That(host.GetProperty("room").GetProperty("boundaries")[0].GetProperty("segments")[0].GetProperty("elementId").GetInt64()).IsEqualTo(9L);
        new LinkReadTransform([0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]).Apply(details, 42, "MEP");
        using var linkedJson = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<ElementDetailsData>.Ok("element-details", details, 1)));
        var linked = linkedJson.RootElement.GetProperty("data");
        await Assert.That(linked.GetProperty("linkedElementId").GetInt64()).IsEqualTo(7L);
        foreach (var reference in new[] { linked.GetProperty("element"), linked.GetProperty("typeElement") })
        {
            await Assert.That(reference.TryGetProperty("id", out _)).IsFalse();
            await Assert.That(reference.GetProperty("linkedElementId").GetInt64()).IsGreaterThan(0L);
        }
        var boundary = linked.GetProperty("room").GetProperty("boundaries")[0].GetProperty("segments")[0];
        await Assert.That(boundary.TryGetProperty("elementId", out _)).IsFalse();
        await Assert.That(boundary.GetProperty("linkedElementId").GetInt64()).IsEqualTo(9L);
    }

    [Test]
    public async Task LinkedGeometry_RoundsRotatedAndTranslatedCoordinatesToTenths()
    {
        var diagonal = Math.Sqrt(0.5);
        var row = new QueryElementItem { RoomCenterMm = [1.1, 2.2, 3.3] };
        new LinkReadTransform([0.03, 0.04, 0.06], [diagonal, diagonal, 0], [-diagonal, diagonal, 0], [0, 0, 1]).Apply(row, 42, "MEP");
        await Assert.That(row.RoomCenterMm[0]).IsEqualTo(-0.7);
        await Assert.That(row.RoomCenterMm[1]).IsEqualTo(2.4);
        await Assert.That(row.RoomCenterMm[2]).IsEqualTo(3.4);
    }

    [Test]
    public async Task PublicJobParser_RefusesViewWithLinkButPreservesHostViews()
    {
        var linked = ControlJobParser.Parse("""{"command":"query-elements","link":"*","view":"Host Level 1"}""");
        await Assert.That(linked.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(linked.Error).Contains("view cannot be combined with link");
        var host = ControlJobParser.Parse("""{"command":"query-elements","view":"Host Level 1"}""");
        await Assert.That(host.Error).IsNull();
        await Assert.That(host.Filters.View).IsEqualTo("Host Level 1");
    }

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
