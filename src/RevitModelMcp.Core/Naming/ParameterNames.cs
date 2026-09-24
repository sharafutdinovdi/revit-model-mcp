using System.Text.RegularExpressions;

namespace RevitModelMcp.Core.Naming;

/// <summary>
/// Locale-independent parameter names. Revit reports parameter names in the UI language, so a request that
/// does not match a localized name falls back to <c>BuiltInParameter</c> names: the enum name itself
/// (<c>ALL_MODEL_INSTANCE_COMMENTS</c>) or the English label of a common built-in ("Comments").
/// </summary>
public static class ParameterNames
{
    private static readonly Regex EnumName = new("^[A-Za-z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$", RegexOptions.Compiled);

    // Ordered by preference: the first built-in present on the element wins.
    private static readonly Dictionary<string, string[]> EnglishBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Comments"] = ["ALL_MODEL_INSTANCE_COMMENTS"],
        ["Type Comments"] = ["ALL_MODEL_TYPE_COMMENTS"],
        ["Mark"] = ["ALL_MODEL_MARK"],
        ["Type Mark"] = ["ALL_MODEL_TYPE_MARK", "WINDOW_TYPE_ID"],
        ["Description"] = ["ALL_MODEL_DESCRIPTION"],
        ["Model"] = ["ALL_MODEL_MODEL"],
        ["Manufacturer"] = ["ALL_MODEL_MANUFACTURER"],
        ["URL"] = ["ALL_MODEL_URL"],
        ["Cost"] = ["ALL_MODEL_COST"],
        ["Keynote"] = ["KEYNOTE_PARAM"],
        ["Level"] = ["FAMILY_LEVEL_PARAM", "LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "INSTANCE_REFERENCE_LEVEL_PARAM", "RBS_START_LEVEL_PARAM"],
        ["Reference Level"] = ["INSTANCE_REFERENCE_LEVEL_PARAM", "RBS_START_LEVEL_PARAM"],
        ["Base Constraint"] = ["WALL_BASE_CONSTRAINT"],
        ["Offset"] = ["INSTANCE_FREE_HOST_OFFSET_PARAM", "INSTANCE_ELEVATION_PARAM", "RBS_OFFSET_PARAM", "FLOOR_HEIGHTABOVELEVEL_PARAM", "WALL_BASE_OFFSET"],
        ["Elevation from Level"] = ["INSTANCE_ELEVATION_PARAM"],
        ["Height Offset From Level"] = ["FLOOR_HEIGHTABOVELEVEL_PARAM"],
        ["Base Offset"] = ["WALL_BASE_OFFSET"],
        ["Top Offset"] = ["WALL_TOP_OFFSET"],
        ["Unconnected Height"] = ["WALL_USER_HEIGHT_PARAM"],
        ["Sill Height"] = ["INSTANCE_SILL_HEIGHT_PARAM"],
        ["Head Height"] = ["INSTANCE_HEAD_HEIGHT_PARAM"],
        ["Phase Created"] = ["PHASE_CREATED"],
        ["Phase Demolished"] = ["PHASE_DEMOLISHED"]
    };

    /// <summary>
    /// <c>BuiltInParameter</c> names to try, in order, once the localized name did not match. Empty when the
    /// request is neither an enum-style name nor a known English label.
    /// </summary>
    public static IReadOnlyList<string> BuiltInCandidates(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return [];
        var trimmed = requested.Trim();
        var result = new List<string>();
        if (EnumName.IsMatch(trimmed)) result.Add(trimmed.ToUpperInvariant());
        if (EnglishBuiltIns.TryGetValue(trimmed, out var builtIns))
            result.AddRange(builtIns.Where(name => !result.Contains(name)));
        return result;
    }
}
