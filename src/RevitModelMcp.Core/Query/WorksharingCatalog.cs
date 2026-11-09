using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Query;

public static class WorksharingCatalog
{
    public const int MaximumCountElements = 100000;

    public static void RequireWorkshared(bool isWorkshared)
    {
        if (!isWorkshared) throw new InvalidOperationException("Workset and ownership reads require a workshared document.");
    }

    public static Dictionary<long, int> CountElements(IEnumerable<long> worksetIds, SkippedReadDiagnostics diagnostics)
    {
        var counts = new Dictionary<long, int>();
        var processed = 0;
        foreach (var worksetId in worksetIds)
        {
            if (processed++ == MaximumCountElements)
            {
                diagnostics.Add("workset element counts", new InvalidOperationException(
                    $"Count scan stopped at {MaximumCountElements} loaded non-type elements; counts are lower bounds."));
                break;
            }
            counts[worksetId] = counts.GetValueOrDefault(worksetId) + 1;
        }
        return counts;
    }

    public static CatalogData Page(string section, IEnumerable<CatalogItem> items, string? nameContains, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(limit));
        var filtered = items.Where(item => nameContains is null ||
            item.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        return new CatalogData
        {
            Section = section,
            Items = filtered.Skip(offset).Take(limit).ToList(),
            Total = filtered.Count,
            HasMore = (long)offset + limit < filtered.Count,
            NextOffset = Math.Min((long)offset + limit, filtered.Count)
        };
    }

    public static CatalogData ReadOwnership(bool isWorkshared, IEnumerable<long> elementIds,
        Func<long, CatalogItem?> read, int offset, int limit, SkippedReadDiagnostics diagnostics)
    {
        RequireWorkshared(isWorkshared);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new CatalogData { Section = "ownership", NextOffset = offset };
        var index = 0;
        foreach (var elementId in elementIds)
        {
            result.Total++;
            if (index++ < offset) continue;
            if ((long)index > (long)offset + limit) continue;
            result.NextOffset = index;
            try
            {
                var item = read(elementId);
                if (item is not null) result.Items.Add(item);
            }
            catch (Exception exception) { diagnostics.Add($"element {elementId} ownership", exception); }
        }
        result.HasMore = result.NextOffset < result.Total;
        return result;
    }
}
