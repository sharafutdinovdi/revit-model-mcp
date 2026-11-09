using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Capture;

internal static class WorksharingCatalogReader
{
    public static CatalogData Read(Document document, string section, string? nameContains, int offset, int limit)
    {
        WorksharingCatalog.RequireWorkshared(document.IsWorkshared);
        var diagnostics = SkippedReadDiagnostics.Current!;
        using var worksets = new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset);
        var userWorksets = worksets.ToWorksets();
        foreach (var workset in userWorksets.Where(workset => !workset.IsOpen))
            diagnostics.Add($"workset {workset.Id.IntegerValue}", new InvalidOperationException(
                "Closed workset: unloaded elements are unavailable; counts and ownership cover loaded elements only."));
        using var elements = new FilteredElementCollector(document).WhereElementIsNotElementType();
        if (section.Equals("ownership", StringComparison.OrdinalIgnoreCase))
            return WorksharingCatalog.ReadOwnership(true, ElementIds(elements), id => ReadOwnership(document, id), offset, limit, diagnostics);

        var sampled = 0;
        IEnumerable<long> WorksetIds()
        {
            foreach (var element in elements)
            {
                if (sampled == WorksharingCatalog.MaximumCountElements)
                {
                    diagnostics.Add("workset element counts", new InvalidOperationException(
                        $"Count scan stopped at {WorksharingCatalog.MaximumCountElements} loaded non-type elements; counts are lower bounds."));
                    yield break;
                }
                sampled++;
                long? worksetId = null;
                try { worksetId = element.WorksetId.IntegerValue; }
                catch (Exception exception) { diagnostics.Add($"element {RevitValueReader.GetId(element.Id)} workset", exception); }
                if (worksetId.HasValue) yield return worksetId.Value;
            }
        }
        var counts = WorksharingCatalog.CountElements(WorksetIds(), diagnostics);
        var items = new List<CatalogItem>();
        foreach (var workset in userWorksets)
        {
            try
            {
                items.Add(new CatalogItem
                {
                    Id = workset.Id.IntegerValue,
                    Name = workset.Name,
                    Type = workset.Kind.ToString(),
                    Owner = workset.Owner,
                    IsOpen = workset.IsOpen,
                    Count = counts.GetValueOrDefault(workset.Id.IntegerValue)
                });
            }
            catch (Exception exception) { diagnostics.Add($"workset {workset.Id.IntegerValue}", exception); }
        }
        return WorksharingCatalog.Page("worksets", items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase), nameContains, offset, limit);
    }

    private static IEnumerable<long> ElementIds(FilteredElementCollector collector)
    {
        using var iterator = collector.GetElementIdIterator();
        while (iterator.MoveNext()) yield return RevitValueReader.GetId(iterator.Current);
    }

    private static CatalogItem? ReadOwnership(Document document, long id)
    {
#if REVIT2022 || REVIT2023
        var elementId = new ElementId(checked((int)id));
#else
        var elementId = new ElementId(id);
#endif
        var status = WorksharingUtils.GetCheckoutStatus(document, elementId);
        if (status == CheckoutStatus.NotOwned) return null;
        using var tooltip = WorksharingUtils.GetWorksharingTooltipInfo(document, elementId);
        var element = document.GetElement(elementId);
        return new CatalogItem
        {
            Id = id,
            Name = element.Name,
            WorksetId = element.WorksetId.IntegerValue,
            Owner = tooltip.Owner,
            CheckoutStatus = status.ToString()
        };
    }
}
