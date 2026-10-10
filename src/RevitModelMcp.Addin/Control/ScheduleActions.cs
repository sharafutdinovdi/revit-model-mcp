using System.Globalization;
using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class ScheduleActions
{
    /// <summary>Creates a single-category schedule with named fields, filters and sorting; unresolved inputs are reported in <c>skipped</c>.</summary>
    internal static ActionResultData Create(Document document, ActionJobContract action)
    {
        var categories = document.Settings.Categories.Cast<Category>().ToList();
        var category = ViewVisibility.ResolveCategory(action.Category!, categories)
            ?? throw new ArgumentException($"Category '{action.Category}' was not found.");
        var schedule = ViewSchedule.CreateSchedule(document, category.Id);
        var definition = schedule.Definition;
        var schedulable = definition.GetSchedulableFields();
        var added = new List<ScheduleField>();
        var skipped = new List<string>();
        var resolved = new List<ScheduleFieldResult>();

        ScheduleField? Resolve(string name, bool hiddenIfNew)
        {
            var existing = added.FirstOrDefault(field => SameName(field.GetName(), name));
            if (existing is not null) return existing;
            var match = schedulable.FirstOrDefault(field => SameName(field.GetName(document), name));
            if (match is null) return null;
            var field = definition.AddField(match);
            field.IsHidden = hiddenIfNew;
            added.Add(field);
            return field;
        }

        foreach (var name in action.ScheduleFields!)
        {
            if (added.Any(field => !field.IsHidden && SameName(field.GetName(), name)))
            {
                skipped.Add($"Field '{name}': already added.");
                continue;
            }
            var field = Resolve(name, false);
            if (field is null)
            {
                skipped.Add($"Field '{name}': no schedulable field with this name in category '{category.Name}'.");
                continue;
            }
            field.IsHidden = false;
            resolved.Add(new ScheduleFieldResult { Requested = name, Name = field.GetName() });
        }
        if (resolved.Count == 0)
            throw new ArgumentException($"None of the fields exist in category '{category.Name}'. Available: {AvailableNames(document, schedulable)}.");

        var filtersApplied = 0;
        foreach (var filter in action.ScheduleFilters ?? [])
        {
            var field = Resolve(filter.Field!, true);
            if (field is null)
            {
                skipped.Add($"Filter on '{filter.Field}': no schedulable field with this name.");
                continue;
            }
            var failure = TryAddFilter(definition, field, filter);
            if (failure is null) filtersApplied++;
            else skipped.Add($"Filter on '{filter.Field}': {failure}");
        }

        var sortApplied = 0;
        foreach (var sort in action.ScheduleSort ?? [])
        {
            var field = Resolve(sort.Field!, true);
            if (field is null)
            {
                skipped.Add($"Sort on '{sort.Field}': no schedulable field with this name.");
                continue;
            }
            definition.AddSortGroupField(new ScheduleSortGroupField(field.FieldId,
                sort.Descending ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending)
            {
                ShowHeader = sort.Header,
                ShowFooter = sort.Footer,
                ShowBlankLine = sort.BlankLine
            });
            sortApplied++;
        }

        definition.IsItemized = action.Itemized;
        ActionMutations.SetViewName(document, schedule, action.Name);

        var id = RevitValueReader.GetId(schedule.Id);
        var result = new ActionResultData
        {
            Id = id,
            ViewId = id,
            ViewName = schedule.Name,
            Category = "Schedule",
            Count = resolved.Count,
            Fields = resolved,
            FiltersApplied = filtersApplied,
            SortApplied = sortApplied,
            Verification = new ActionVerification { Changed = [id] }
        };
        if (skipped.Count > 0)
        {
            result.Skipped = skipped;
            result.Warning = $"Skipped {skipped.Count} input(s); see skipped. Available fields: {AvailableNames(document, schedulable)}.";
        }
        return result;
    }

    private static string? TryAddFilter(ScheduleDefinition definition, ScheduleField field, ScheduleFilterContract filter)
    {
        var type = FilterType(filter.Operator!);
        if (!ScheduleFilterOperators.RequiresValue(filter.Operator!))
            return TryAdd(definition, new ScheduleFilter(field.FieldId, type));

        var text = filter.Value!;
        var candidates = new List<Func<ScheduleFilter>> { () => new ScheduleFilter(field.FieldId, type, text) };
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            candidates.Add(() => new ScheduleFilter(field.FieldId, type, ToInternal(field, number)));
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                candidates.Add(() => new ScheduleFilter(field.FieldId, type, whole));
        }
        string? lastFailure = null;
        foreach (var candidate in candidates)
        {
            lastFailure = TryAdd(definition, candidate());
            if (lastFailure is null) return null;
        }
        return lastFailure;
    }

    private static string? TryAdd(ScheduleDefinition definition, ScheduleFilter filter)
    {
        try
        {
            definition.AddFilter(filter);
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return exception.Message.Split('\n')[0].Trim();
        }
    }

    /// <summary>Lengths are millimetres, areas m2 and volumes m3; other numbers are used as stored.</summary>
    private static double ToInternal(ScheduleField field, double value)
    {
        var spec = field.GetSpecTypeId();
        if (spec.Equals(SpecTypeId.Length)) return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);
        if (spec.Equals(SpecTypeId.Area)) return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.SquareMeters);
        if (spec.Equals(SpecTypeId.Volume)) return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.CubicMeters);
        return value;
    }

    private static ScheduleFilterType FilterType(string value) => value switch
    {
        "equal" => ScheduleFilterType.Equal,
        "not_equal" => ScheduleFilterType.NotEqual,
        "greater_than" => ScheduleFilterType.GreaterThan,
        "greater_than_or_equal" => ScheduleFilterType.GreaterThanOrEqual,
        "less_than" => ScheduleFilterType.LessThan,
        "less_than_or_equal" => ScheduleFilterType.LessThanOrEqual,
        "contains" => ScheduleFilterType.Contains,
        "not_contains" => ScheduleFilterType.NotContains,
        "begins_with" => ScheduleFilterType.BeginsWith,
        "not_begins_with" => ScheduleFilterType.NotBeginsWith,
        "ends_with" => ScheduleFilterType.EndsWith,
        "not_ends_with" => ScheduleFilterType.NotEndsWith,
        "has_value" => ScheduleFilterType.HasValue,
        "has_no_value" => ScheduleFilterType.HasNoValue,
        _ => throw new ArgumentException($"Unknown filter operator '{value}'.")
    };

    private static bool SameName(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string AvailableNames(Document document, IList<SchedulableField> fields) =>
        string.Join(", ", fields.Select(field => field.GetName(document)).Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(40));
}
