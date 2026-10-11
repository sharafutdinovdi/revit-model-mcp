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
            var failure = TryAddFilter(document, category, definition, field, filter);
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

    private static string? TryAddFilter(Document document, Category category, ScheduleDefinition definition,
        ScheduleField field, ScheduleFilterContract filter)
    {
        var type = FilterType(filter.Operator!);
        if (!ScheduleFilterOperators.RequiresValue(filter.Operator!))
            return TryAdd(definition, new ScheduleFilter(field.FieldId, type));

        var text = filter.Value!;
        var samples = SampleParameters(document, category, field);
        var storage = samples.Count == 0 ? null : samples[0].StorageType.ToString();
        string? lastFailure = null;
        foreach (var kind in ScheduleFilterValuePlan.Plan(storage, text))
        {
            ScheduleFilter? candidate = null;
            switch (kind)
            {
                case ScheduleFilterValueKind.Text:
                    candidate = new ScheduleFilter(field.FieldId, type, text);
                    break;
                case ScheduleFilterValueKind.Number:
                    candidate = new ScheduleFilter(field.FieldId, type,
                        ToInternal(field, double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)));
                    break;
                case ScheduleFilterValueKind.Integer:
                    candidate = new ScheduleFilter(field.FieldId, type, int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case ScheduleFilterValueKind.ElementIdLiteral:
                    candidate = new ScheduleFilter(field.FieldId, type, CreateId(long.Parse(text, CultureInfo.InvariantCulture)));
                    break;
                case ScheduleFilterValueKind.ElementName:
                    var resolution = ScheduleFilterValuePlan.ResolveName(ReferencedElements(document, samples), text);
                    if (resolution.Id is null) { lastFailure = resolution.Error; continue; }
                    candidate = new ScheduleFilter(field.FieldId, type, CreateId(resolution.Id.Value));
                    break;
            }
            lastFailure = TryAdd(definition, candidate!);
            if (lastFailure is null) return null;
        }
        return lastFailure;
    }

    private static ElementId CreateId(long value) => ActionCommandExecutor.CreateId(value);

    /// <summary>The field's parameter on up to 200 category elements, used to read the storage type and referenced elements.</summary>
    private static List<Parameter> SampleParameters(Document document, Category category, ScheduleField field)
    {
        var result = new List<Parameter>();
        var builtIn = RevitValueReader.GetId(field.ParameterId);
        var name = field.GetName();
        using var collector = new FilteredElementCollector(document).OfCategoryId(category.Id).WhereElementIsNotElementType();
        foreach (var element in collector)
        {
            var parameter = RevitValueReader.ToBuiltInParameter(builtIn) is { } builtInParameter
                ? element.get_Parameter(builtInParameter)
                : null;
            parameter ??= element.LookupParameter(name);
            if (parameter is not null) result.Add(parameter);
            if (result.Count >= 200) break;
        }
        return result;
    }

    /// <summary>Elements the sampled ElementId parameters point at, plus all levels and phases.</summary>
    private static IEnumerable<(long Id, string Name)> ReferencedElements(Document document, List<Parameter> samples)
    {
        var seen = new HashSet<long>();
        foreach (var parameter in samples)
        {
            if (parameter.StorageType != StorageType.ElementId) continue;
            var referenced = document.GetElement(parameter.AsElementId());
            if (referenced is not null && seen.Add(RevitValueReader.GetId(referenced.Id)))
                yield return (RevitValueReader.GetId(referenced.Id), referenced.Name);
        }
        foreach (var type in new[] { typeof(Level), typeof(Phase) })
        {
            using var collector = new FilteredElementCollector(document).OfClass(type);
            foreach (var element in collector)
                if (seen.Add(RevitValueReader.GetId(element.Id)))
                    yield return (RevitValueReader.GetId(element.Id), element.Name);
        }
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
