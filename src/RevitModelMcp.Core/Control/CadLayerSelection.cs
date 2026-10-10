namespace RevitModelMcp.Core.Control;

/// <summary>Checks requested CAD layer names against the layers of an import, ignoring case.</summary>
public static class CadLayerSelection
{
    public static List<string> Missing(IEnumerable<string> requested, IEnumerable<string> available)
    {
        var known = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        return requested.Where(layer => !known.Contains(layer)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string UnknownMessage(IReadOnlyList<string> missing, IEnumerable<string> available)
    {
        var layers = available.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(layer => layer, StringComparer.OrdinalIgnoreCase).ToList();
        return $"CAD layer{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing.Select(layer => $"'{layer}'"))} not found. Available layers: {(layers.Count == 0 ? "none" : string.Join(", ", layers))}.";
    }

    public static string NoWallsMessage(int unpairedLines) =>
        $"No walls could be paired from the selected layers ({unpairedLines} unpaired lines). Nothing was created.";
}
