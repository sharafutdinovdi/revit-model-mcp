using System.Globalization;
using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Capture;

internal sealed class QueryFilterContext
{
    public Func<FilteredElementCollector> CreateCollector { get; set; } = null!;

    public IReadOnlyList<string> ResolvedFields { get; set; } = Array.Empty<string>();
}

internal static class QueryFilterBuilder
{
    public static QueryFilterContext Build(Document document, ElementFilterSpec filters, IEnumerable<string> fieldNames)
    {
        var requestedFields = fieldNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var structuralFilters = BuildStructuralFilters(document, filters, out var viewId);
        Func<FilteredElementCollector> structuralCollector = () => Apply(
            viewId is null ? new FilteredElementCollector(document) : new FilteredElementCollector(document, viewId),
            structuralFilters);
        var parameterNames = filters.Parameters.Select(filter => filter.Parameter).Concat(requestedFields);
        var parameters = QueryParameterResolver.Resolve(document, parameterNames, structuralCollector);
        var parameterFilters = filters.Parameters.Select(filter => BuildParameterFilter(document, filter, parameters[filter.Parameter]));
        var allFilters = structuralFilters.Concat(parameterFilters).ToList();
        return new QueryFilterContext
        {
            ResolvedFields = requestedFields
                .Where(ElementFieldReader.IsBuiltInField)
                .Concat(parameters.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            CreateCollector = () => Apply(
                viewId is null ? new FilteredElementCollector(document) : new FilteredElementCollector(document, viewId),
                allFilters)
        };
    }

    private static FilteredElementCollector Apply(FilteredElementCollector collector, IEnumerable<ElementFilter> filters)
    {
        collector.WhereElementIsNotElementType();
        foreach (var filter in filters)
        {
            collector.WherePasses(filter);
        }

        return collector;
    }

    private static List<ElementFilter> BuildStructuralFilters(
        Document document,
        ElementFilterSpec filters,
        out ElementId? viewId)
    {
        var result = new List<ElementFilter>();
        viewId = null;
        if (filters.Categories.Count > 0)
        {
            var categoryIds = ResolveCategories(document, filters.Categories);
            result.Add(new ElementMulticategoryFilter(categoryIds));
        }

        if (filters.View is not null)
        {
            viewId = ResolveView(document, filters.View).Id;
        }

        if (filters.Level is not null)
        {
            result.Add(new ElementLevelFilter(ResolveLevel(document, filters.Level).Id));
        }

        if (filters.Workset is not null)
        {
            result.Add(new ElementWorksetFilter(ResolveWorkset(document, filters.Workset).Id));
        }

        AddPhaseRule(result, ResolvePhase(document, filters.Phase));
        AddElementIdRule(result, BuiltInParameter.AREA_SCHEME_ID, ResolveAreaScheme(document, filters.AreaScheme));
        AddStringRule(result, BuiltInParameter.ALL_MODEL_FAMILY_NAME, filters.Family);
        AddStringRule(result, BuiltInParameter.ALL_MODEL_TYPE_NAME, filters.Type);
        return result;
    }

    private static ElementFilter BuildParameterFilter(
        Document document,
        ParameterFilterSpec filter,
        QueryParameterDescriptor descriptor)
    {
        FilterRule rule;
        switch (filter.Operator)
        {
            case ParameterOperator.Empty:
                rule = ParameterFilterRuleFactory.CreateHasNoValueParameterRule(descriptor.Id);
                return new ElementParameterFilter(rule);
            case ParameterOperator.NotEmpty:
                rule = ParameterFilterRuleFactory.CreateHasValueParameterRule(descriptor.Id);
                return new ElementParameterFilter(rule);
            case ParameterOperator.Exists:
                return new LogicalOrFilter(
                    new ElementParameterFilter(ParameterFilterRuleFactory.CreateHasValueParameterRule(descriptor.Id)),
                    new ElementParameterFilter(ParameterFilterRuleFactory.CreateHasNoValueParameterRule(descriptor.Id)));
            case ParameterOperator.Contains:
                if (descriptor.Kind is not (QueryParameterKind.String or QueryParameterKind.Unknown))
                {
                    throw new ArgumentException(
                        $"Оператор contains применим только к текстовому параметру; «{descriptor.Name}» имеет тип {descriptor.Kind}.");
                }

                rule = CreateContainsRule(descriptor.Id, filter.Value!);
                return new ElementParameterFilter(rule);
        }

        rule = descriptor.Kind switch
        {
            QueryParameterKind.Double => BuildDoubleRule(document, descriptor, filter),
            QueryParameterKind.Integer => BuildIntegerRule(descriptor, filter),
            QueryParameterKind.ElementId => BuildElementIdRule(descriptor, filter),
            _ => BuildStringRule(descriptor, filter)
        };
        return new ElementParameterFilter(rule);
    }

    private static FilterRule BuildDoubleRule(
        Document document,
        QueryParameterDescriptor descriptor,
        ParameterFilterSpec filter)
    {
        var displayValue = ParseDouble(filter.Value!, descriptor.Name);
        var internalValue = displayValue;
        if (descriptor.DataType is not null && UnitUtils.IsMeasurableSpec(descriptor.DataType))
        {
            var unit = descriptor.DataType == SpecTypeId.Length
                ? UnitTypeId.Millimeters
                : descriptor.DataType == SpecTypeId.Area
                    ? UnitTypeId.SquareMeters
                    : descriptor.DataType == SpecTypeId.Volume
                        ? UnitTypeId.CubicMeters
                        : document.GetUnits().GetFormatOptions(descriptor.DataType).GetUnitTypeId();
            internalValue = UnitUtils.ConvertToInternalUnits(displayValue, unit);
        }

        const double epsilon = 1e-9;
        return filter.Operator switch
        {
            ParameterOperator.Equals => ParameterFilterRuleFactory.CreateEqualsRule(descriptor.Id, internalValue, epsilon),
            ParameterOperator.Greater => ParameterFilterRuleFactory.CreateGreaterRule(descriptor.Id, internalValue, epsilon),
            ParameterOperator.Less => ParameterFilterRuleFactory.CreateLessRule(descriptor.Id, internalValue, epsilon),
            _ => throw Unsupported(filter)
        };
    }

    private static FilterRule BuildIntegerRule(QueryParameterDescriptor descriptor, ParameterFilterSpec filter)
    {
        if (!int.TryParse(filter.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"Значение «{filter.Value}» параметра «{descriptor.Name}» не является целым числом.");
        }

        return filter.Operator switch
        {
            ParameterOperator.Equals => ParameterFilterRuleFactory.CreateEqualsRule(descriptor.Id, value),
            ParameterOperator.Greater => ParameterFilterRuleFactory.CreateGreaterRule(descriptor.Id, value),
            ParameterOperator.Less => ParameterFilterRuleFactory.CreateLessRule(descriptor.Id, value),
            _ => throw Unsupported(filter)
        };
    }

    private static FilterRule BuildElementIdRule(QueryParameterDescriptor descriptor, ParameterFilterSpec filter)
    {
        if (!long.TryParse(filter.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return BuildStringRule(descriptor, filter);
        }

        return filter.Operator switch
        {
            ParameterOperator.Equals => ParameterFilterRuleFactory.CreateEqualsRule(descriptor.Id, CreateElementId(value)),
            ParameterOperator.Greater => ParameterFilterRuleFactory.CreateGreaterRule(descriptor.Id, CreateElementId(value)),
            ParameterOperator.Less => ParameterFilterRuleFactory.CreateLessRule(descriptor.Id, CreateElementId(value)),
            _ => throw Unsupported(filter)
        };
    }

    private static FilterRule BuildStringRule(QueryParameterDescriptor descriptor, ParameterFilterSpec filter)
    {
        return filter.Operator switch
        {
            ParameterOperator.Equals => CreateEqualsRule(descriptor.Id, filter.Value!),
            ParameterOperator.Greater => CreateGreaterRule(descriptor.Id, filter.Value!),
            ParameterOperator.Less => CreateLessRule(descriptor.Id, filter.Value!),
            _ => throw Unsupported(filter)
        };
    }

    private static List<ElementId> ResolveCategories(Document document, IReadOnlyList<string> names)
    {
        var categories = document.Settings.Categories.Cast<Category>().ToList();
        var result = new List<ElementId>();
        foreach (var name in names)
        {
            // Подписи берём из самого Revit, а BuiltInCategory оставляем английским алиасом без словаря.
            var category = CategoryNameResolver.Resolve(name, categories, GetRevitCategoryNames, GetBuiltInCategoryName);
            result.Add(category.Id);
        }

        return result;
    }

    private static string? GetBuiltInCategoryName(Category category)
    {
        var builtInCategory = GetBuiltInCategory(category);
        return builtInCategory?.ToString();
    }

    private static IEnumerable<string> GetRevitCategoryNames(Category category)
    {
        yield return category.Name;
        var builtInCategory = GetBuiltInCategory(category);
        if (builtInCategory is null)
        {
            yield break;
        }

        string? localizedName = null;
        try
        {
            localizedName = LabelUtils.GetLabelFor(builtInCategory.Value);
        }
        catch
        {
            // Не у каждой служебной категории есть отображаемая подпись.
        }

        if (!string.IsNullOrWhiteSpace(localizedName))
        {
            yield return localizedName!;
        }
    }

    private static BuiltInCategory? GetBuiltInCategory(Category category)
    {
        var value = RevitValueReader.GetId(category.Id);
        if (value is < int.MinValue or > int.MaxValue)
        {
            return null;
        }

        var builtInCategory = (BuiltInCategory)(int)value;
        return Enum.IsDefined(typeof(BuiltInCategory), builtInCategory) ? builtInCategory : null;
    }

    private static View ResolveView(Document document, string name) =>
        new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(view => !view.IsTemplate && string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw NotFound("Вид", name, "views");

    private static Level ResolveLevel(Document document, string name) =>
        new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(level => string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw NotFound("Уровень", name, "levels");

    private static Workset ResolveWorkset(Document document, string name) =>
        new FilteredWorksetCollector(document).ToWorksets()
            .FirstOrDefault(workset => string.Equals(workset.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw NotFound("Рабочий набор", name, "worksets");

    private static Phase? ResolvePhase(Document document, string? name) => name is null
        ? null
        : document.Phases.Cast<Phase>().FirstOrDefault(phase => string.Equals(phase.Name, name, StringComparison.OrdinalIgnoreCase))
          ?? throw NotFound("Фаза", name, "phases");

    private static AreaScheme? ResolveAreaScheme(Document document, string? name) => name is null
        ? null
        : new FilteredElementCollector(document).OfClass(typeof(AreaScheme)).Cast<AreaScheme>()
            .FirstOrDefault(scheme => string.Equals(scheme.Name, name, StringComparison.OrdinalIgnoreCase))
          ?? throw NotFound("Схема зонирования", name, "area-schemes");

    private static void AddElementIdRule(List<ElementFilter> filters, BuiltInParameter parameter, Element? value)
    {
        if (value is not null)
        {
            filters.Add(new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(
                CreateElementId((long)parameter), value.Id)));
        }
    }

    private static void AddPhaseRule(List<ElementFilter> filters, Phase? phase)
    {
        if (phase is null)
        {
            return;
        }

        filters.Add(new LogicalOrFilter(
            new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(
                CreateElementId((long)BuiltInParameter.PHASE_CREATED), phase.Id)),
            new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(
                CreateElementId((long)BuiltInParameter.ROOM_PHASE_ID), phase.Id))));
    }

    private static void AddStringRule(List<ElementFilter> filters, BuiltInParameter parameter, string? value)
    {
        if (value is not null)
        {
            filters.Add(new ElementParameterFilter(CreateEqualsRule(CreateElementId((long)parameter), value)));
        }
    }

    private static double ParseDouble(string value, string parameter) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Значение «{value}» параметра «{parameter}» не является числом.");

    private static ArgumentException NotFound(string kind, string name, string section) =>
        new($"{kind} «{name}» не найден. Посмотрите list-catalog с section={section}.");

    private static NotSupportedException Unsupported(ParameterFilterSpec filter) =>
        new($"Оператор {filter.Operator} не поддерживается для параметра «{filter.Parameter}».");

    private static FilterRule CreateContainsRule(ElementId id, string value)
    {
#if !REVIT2023_OR_GREATER
        return ParameterFilterRuleFactory.CreateContainsRule(id, value, false);
#else
        return ParameterFilterRuleFactory.CreateContainsRule(id, value);
#endif
    }

    private static FilterRule CreateEqualsRule(ElementId id, string value)
    {
#if !REVIT2023_OR_GREATER
        return ParameterFilterRuleFactory.CreateEqualsRule(id, value, false);
#else
        return ParameterFilterRuleFactory.CreateEqualsRule(id, value);
#endif
    }

    private static FilterRule CreateGreaterRule(ElementId id, string value)
    {
#if !REVIT2023_OR_GREATER
        return ParameterFilterRuleFactory.CreateGreaterRule(id, value, false);
#else
        return ParameterFilterRuleFactory.CreateGreaterRule(id, value);
#endif
    }

    private static FilterRule CreateLessRule(ElementId id, string value)
    {
#if !REVIT2023_OR_GREATER
        return ParameterFilterRuleFactory.CreateLessRule(id, value, false);
#else
        return ParameterFilterRuleFactory.CreateLessRule(id, value);
#endif
    }

    private static ElementId CreateElementId(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }
}
