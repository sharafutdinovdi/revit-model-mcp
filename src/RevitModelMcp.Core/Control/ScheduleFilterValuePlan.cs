namespace RevitModelMcp.Core.Control;

/// <summary>How a schedule filter value text is turned into the typed value Revit expects.</summary>
public enum ScheduleFilterValueKind
{
    /// <summary>The text as given.</summary>
    Text,

    /// <summary>A decimal number in the client's units.</summary>
    Number,

    /// <summary>A whole number stored as an integer.</summary>
    Integer,

    /// <summary>The name of the element the field references (Level, Phase, ...), filtered by its ElementId.</summary>
    ElementName,

    /// <summary>A whole number used as a raw ElementId.</summary>
    ElementIdLiteral
}

/// <summary>Outcome of resolving a name to one element ID.</summary>
public sealed record ScheduleNameResolution(long? Id, string? Error);

/// <summary>
/// Chooses the value types to try for a schedule filter. Fields that store an ElementId (Level, Phase, ...) only
/// accept an ElementId filter value, so a name must be resolved to the element and filtered by its ID.
/// </summary>
public static class ScheduleFilterValuePlan
{
    /// <summary>Ordered value kinds to try for a field with the given parameter storage type (null when unknown).</summary>
    public static IReadOnlyList<ScheduleFilterValueKind> Plan(string? storageType, string text)
    {
        var isNumber = double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);
        var isWhole = int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out _);
        var kinds = new List<ScheduleFilterValueKind>();
        switch (storageType)
        {
            case "ElementId":
                kinds.Add(ScheduleFilterValueKind.ElementName);
                if (isWhole) kinds.Add(ScheduleFilterValueKind.ElementIdLiteral);
                return kinds;
            case "String":
                return [ScheduleFilterValueKind.Text];
        }
        kinds.Add(ScheduleFilterValueKind.Text);
        if (isNumber) kinds.Add(ScheduleFilterValueKind.Number);
        if (isNumber && isWhole) kinds.Add(ScheduleFilterValueKind.Integer);
        if (storageType is null) kinds.Add(ScheduleFilterValueKind.ElementName);
        return kinds;
    }

    /// <summary>Finds the one element whose name equals the text (case-insensitive, trimmed); reports none or several.</summary>
    public static ScheduleNameResolution ResolveName(IEnumerable<(long Id, string Name)> candidates, string text)
    {
        var wanted = text.Trim();
        var ids = candidates
            .Where(candidate => string.Equals(candidate.Name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Id)
            .Distinct()
            .ToList();
        return ids.Count switch
        {
            1 => new ScheduleNameResolution(ids[0], null),
            0 => new ScheduleNameResolution(null, $"no element named '{wanted}' is referenced by this field."),
            _ => new ScheduleNameResolution(null, $"{ids.Count} elements are named '{wanted}'; filter by element ID instead.")
        };
    }
}
