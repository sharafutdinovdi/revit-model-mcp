using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Datums;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class LinkDatumReader
{
    public static LinkDatumData Read(Document document, LinkDatumJobOptions options)
    {
        var (instance, linkDatums, hostDatums, transform) = Collect(document, options);
        var items = DatumMatcher.Match(linkDatums, hostDatums, transform,
            new DatumMatchOptions(options.Kinds, options.NameMap, options.Prefix, options.Suffix,
                options.LevelOffsetMm, options.ReuseMatching, options.ToleranceMm));
        var data = new LinkDatumData
        {
            Link = new LinkDatumLink { Id = RevitValueReader.GetId(instance.Id), Name = instance.Name },
            ToleranceMm = options.ToleranceMm,
            LevelOffsetMm = options.LevelOffsetMm,
            Items = items
        };
        data.UpdateSummary();
        return data;
    }

    public static (RevitLinkInstance Instance, List<DatumRecord> Link, List<DatumRecord> Host, DatumTransform Transform)
        Collect(Document document, LinkDatumJobOptions options)
    {
        using var collector = new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance));
        var instances = collector.Cast<RevitLinkInstance>().Where(instance => Matches(document, instance, options.Link)).ToList();
        if (instances.Count != 1)
            throw new ArgumentException(instances.Count == 0 ? $"Link '{options.Link}' was not found."
                : "Link reference is ambiguous: " + string.Join(", ", instances.Select(instance =>
                    $"{RevitValueReader.GetId(instance.Id)} {instance.Name}")));
        var selected = instances[0];
        var linkDocument = selected.GetLinkDocument()
            ?? throw new InvalidOperationException($"Link '{selected.Name}' is not loaded.");
        var revitTransform = selected.GetTotalTransform();
        var transform = new DatumTransform(Point(revitTransform.Origin), Point(revitTransform.BasisX),
            Point(revitTransform.BasisY), Point(revitTransform.BasisZ));
        return (selected, CollectDatums(linkDocument, options.Kinds), CollectDatums(document, options.Kinds), transform);
    }

    private static bool Matches(Document document, RevitLinkInstance instance, string reference)
    {
        if (long.TryParse(reference, out var id)) return RevitValueReader.GetId(instance.Id) == id;
        return instance.Name.IndexOf(reference, StringComparison.OrdinalIgnoreCase) >= 0 ||
            (document.GetElement(instance.GetTypeId())?.Name.IndexOf(reference, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static List<DatumRecord> CollectDatums(Document document, IReadOnlyList<string> kinds)
    {
        var result = new List<DatumRecord>();
        if (kinds.Contains("levels"))
        {
            using var collector = new FilteredElementCollector(document).OfClass(typeof(Level));
            result.AddRange(collector.Cast<Level>().Select(level => new DatumRecord(
                RevitValueReader.GetId(level.Id), level.Name, "level", level.ProjectElevation)));
        }
        if (kinds.Contains("grids"))
        {
            using var collector = new FilteredElementCollector(document).OfClass(typeof(Grid));
            foreach (var grid in collector.Cast<Grid>())
            {
                var multiSegment = RevitValueReader.IsValidId(MultiSegmentGrid.GetMultiSegementGridId(grid));
                var curve = grid.Curve;
                result.Add(curve switch
                {
                    Line line => new DatumRecord(RevitValueReader.GetId(grid.Id), grid.Name, "grid",
                        Start: Point(line.GetEndPoint(0)), End: Point(line.GetEndPoint(1)), MultiSegment: multiSegment),
                    Arc arc => new DatumRecord(RevitValueReader.GetId(grid.Id), grid.Name, "grid",
                        Center: Point(arc.Center), Radius: arc.Radius, MultiSegment: multiSegment),
                    _ => new DatumRecord(RevitValueReader.GetId(grid.Id), grid.Name, "grid", MultiSegment: multiSegment)
                });
            }
        }
        return result;
    }

    private static DatumPoint Point(XYZ point) => new(point.X, point.Y, point.Z);
}
