using System.Text.RegularExpressions;

namespace RevitModelMcp.Core.Naming;

/// <summary>
/// Locale-independent category names. A category matches its localized Revit labels, its
/// <c>BuiltInCategory</c> name (<c>OST_StructuralColumns</c>, with or without the prefix) and its English
/// label. Revit's own labels follow the UI language, so the English label is derived from the enum name,
/// with overrides where the API name differs from the English UI label.
/// </summary>
public static class CategoryNames
{
    private static readonly Regex WordBoundary = new("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> EnglishOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OST_PipeCurves"] = "Pipes",
        ["OST_DuctCurves"] = "Ducts",
        ["OST_FlexPipeCurves"] = "Flex Pipes",
        ["OST_FlexDuctCurves"] = "Flex Ducts",
        ["OST_PipeFitting"] = "Pipe Fittings",
        ["OST_DuctFitting"] = "Duct Fittings",
        ["OST_PipeAccessory"] = "Pipe Accessories",
        ["OST_DuctAccessory"] = "Duct Accessories",
        ["OST_DuctTerminal"] = "Air Terminals",
        ["OST_Conduit"] = "Conduits",
        ["OST_ConduitFitting"] = "Conduit Fittings",
        ["OST_CableTray"] = "Cable Trays",
        ["OST_CableTrayFitting"] = "Cable Tray Fittings",
        ["OST_Wire"] = "Wires",
        ["OST_MEPSpaces"] = "Spaces",
        ["OST_HVAC_Zones"] = "HVAC Zones",
        ["OST_GenericModel"] = "Generic Models",
        ["OST_GenericAnnotation"] = "Generic Annotations",
        ["OST_DetailComponents"] = "Detail Items",
        ["OST_SpecialityEquipment"] = "Specialty Equipment",
        ["OST_StructuralFoundation"] = "Structural Foundations",
        ["OST_StructuralTruss"] = "Structural Trusses",
        ["OST_StructuralFramingSystem"] = "Structural Beam Systems",
        ["OST_StructConnections"] = "Structural Connections",
        ["OST_Rebar"] = "Structural Rebar",
        ["OST_CurtainWallPanels"] = "Curtain Panels",
        ["OST_CurtainWallMullions"] = "Curtain Wall Mullions",
        ["OST_Cornices"] = "Wall Sweeps",
        ["OST_Fascia"] = "Fascias",
        ["OST_Gutter"] = "Gutters",
        ["OST_RoofSoffit"] = "Roof Soffits",
        ["OST_EdgeSlab"] = "Slab Edges",
        ["OST_StairsRailing"] = "Railings",
        ["OST_ShaftOpening"] = "Shaft Openings",
        ["OST_Elev"] = "Elevations",
        ["OST_CLines"] = "Reference Planes",
        ["OST_VolumeOfInterest"] = "Scope Boxes",
        ["OST_IOSModelGroups"] = "Model Groups",
        ["OST_IOSDetailGroups"] = "Detail Groups",
        ["OST_RvtLinks"] = "RVT Links",
        ["OST_TextNotes"] = "Text Notes",
        ["OST_MultiCategoryTags"] = "Multi-Category Tags"
    };

    /// <summary>English UI label of a <c>BuiltInCategory</c> name: <c>OST_StructuralColumns</c> → "Structural Columns".</summary>
    public static string? EnglishLabel(string? builtInName)
    {
        if (string.IsNullOrWhiteSpace(builtInName)) return null;
        if (EnglishOverrides.TryGetValue(builtInName!, out var label)) return label;
        var bare = StripPrefix(builtInName!).Replace('_', ' ');
        return WordBoundary.Replace(bare, " ").Trim();
    }

    /// <summary>True when <paramref name="requested"/> names this category in any of the three accepted forms.</summary>
    public static bool Matches(string requested, IEnumerable<string> localizedNames, string? builtInName)
    {
        if (string.IsNullOrWhiteSpace(requested)) return false;
        var trimmed = requested.Trim();
        if (localizedNames.Any(name => string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase))) return true;
        if (builtInName is null) return false;
        if (string.Equals(trimmed, builtInName, StringComparison.OrdinalIgnoreCase)) return true;
        var key = Normalize(trimmed);
        return key == Normalize(StripPrefix(builtInName)) || key == Normalize(EnglishLabel(builtInName) ?? string.Empty);
    }

    /// <summary>All accepted spellings of one category, for close-match suggestions.</summary>
    public static IEnumerable<string> Forms(IEnumerable<string> localizedNames, string? builtInName)
    {
        foreach (var name in localizedNames)
            if (!string.IsNullOrWhiteSpace(name)) yield return name;
        if (builtInName is null) yield break;
        yield return builtInName;
        if (EnglishLabel(builtInName) is { } english) yield return english;
    }

    private static string StripPrefix(string builtInName) =>
        builtInName.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? builtInName.Substring(4) : builtInName;

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
