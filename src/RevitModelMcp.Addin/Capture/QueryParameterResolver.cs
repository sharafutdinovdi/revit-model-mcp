using Autodesk.Revit.DB;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Capture;

internal enum QueryParameterKind
{
    Unknown,
    String,
    Integer,
    Double,
    ElementId
}

internal sealed class QueryParameterDescriptor
{
    public string Name { get; set; } = string.Empty;
    public ElementId Id { get; set; } = ElementId.InvalidElementId;
    public QueryParameterKind Kind { get; set; }
    public ForgeTypeId? DataType { get; set; }
}

internal static class QueryParameterResolver
{
    private static readonly Lazy<(Dictionary<string, BuiltInParameter> Names,
        Dictionary<BuiltInParameter, (string Name, int Order)> Details)> BuiltIns = new(CreateBuiltInCache);

    public static Dictionary<string, QueryParameterDescriptor> Resolve(
        Document document,
        IEnumerable<string> requestedNames,
        Func<FilteredElementCollector> candidateCollector)
    {
        var requested = requestedNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, QueryParameterDescriptor>(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            return result;
        }

        ResolveParameterElements(document, requested, result);
        ResolveBuiltIns(requested, result);
        ResolveFromTypes(document, requested, result);
        ResolveFromCandidates(requested, result, candidateCollector);

        foreach (var name in requested)
        {
            if (!result.ContainsKey(name))
            {
                QueryParameterValidator.Resolve(name, result.Keys);
            }
        }

        return result;
    }

    private static void ResolveParameterElements(
        Document document,
        IReadOnlyList<string> requested,
        IDictionary<string, QueryParameterDescriptor> result)
    {
        var requestedSet = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        foreach (var parameterElement in new FilteredElementCollector(document)
                     .OfClass(typeof(ParameterElement))
                     .Cast<ParameterElement>())
        {
            var definition = parameterElement.GetDefinition();
            if (definition is null || !requestedSet.Contains(definition.Name))
            {
                continue;
            }

            result[definition.Name] = Create(definition.Name, parameterElement.Id, definition.GetDataType());
        }
    }

    private static void ResolveBuiltIns(
        IReadOnlyList<string> requested,
        IDictionary<string, QueryParameterDescriptor> result)
    {
        if (requested.All(result.ContainsKey))
        {
            return;
        }

        var cache = BuiltIns.Value;
        var matches = requested.Where(cache.Names.ContainsKey)
            .GroupBy(name => cache.Names[name])
            .OrderBy(group => cache.Details[group.Key].Order);
        foreach (var match in matches)
        {
            var requestedName = match.First();
            if (result.ContainsKey(requestedName))
            {
                continue;
            }

            result[requestedName] = new QueryParameterDescriptor
            {
                Name = cache.Details[match.Key].Name,
                Id = CreateBuiltInId(match.Key)
            };
        }
    }

    private static (Dictionary<string, BuiltInParameter>, Dictionary<BuiltInParameter, (string, int)>) CreateBuiltInCache()
    {
        var names = new Dictionary<string, BuiltInParameter>(StringComparer.OrdinalIgnoreCase);
        var details = new Dictionary<BuiltInParameter, (string, int)>();
        foreach (BuiltInParameter builtIn in Enum.GetValues(typeof(BuiltInParameter)))
        {
            var enumName = builtIn.ToString();
            string? label = null;
            try
            {
                label = LabelUtils.GetLabelFor(builtIn);
            }
            catch (Exception exception)
            {
                PluginLog.Skipped($"parameter {enumName} display label", exception);
            }

            if (!names.ContainsKey(enumName)) names.Add(enumName, builtIn);
            if (label is not null && !names.ContainsKey(label)) names.Add(label, builtIn);
            if (!details.ContainsKey(builtIn))
                details.Add(builtIn, (string.IsNullOrWhiteSpace(label) ? enumName : label!, details.Count));
        }

        return (names, details);
    }

    private static void ResolveFromTypes(
        Document document,
        IReadOnlyList<string> requested,
        IDictionary<string, QueryParameterDescriptor> result)
    {
        foreach (var type in new FilteredElementCollector(document).WhereElementIsElementType())
        {
            ResolveFromElement(type, requested, result);
            if (AllResolved(requested, result))
            {
                return;
            }
        }
    }

    private static void ResolveFromCandidates(
        IReadOnlyList<string> requested,
        IDictionary<string, QueryParameterDescriptor> result,
        Func<FilteredElementCollector> candidateCollector)
    {
        if (AllResolved(requested, result))
        {
            return;
        }

        foreach (var element in candidateCollector())
        {
            ResolveFromElement(element, requested, result);
            if (AllResolved(requested, result))
            {
                return;
            }
        }
    }

    private static void ResolveFromElement(
        Element element,
        IReadOnlyList<string> requested,
        IDictionary<string, QueryParameterDescriptor> result)
    {
        var requestedSet = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        foreach (Parameter parameter in element.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (name is null || !requestedSet.Contains(name))
            {
                continue;
            }

            if (result.TryGetValue(name, out var existing) && existing.Kind != QueryParameterKind.Unknown)
            {
                continue;
            }

            result[name] = new QueryParameterDescriptor
            {
                Name = name,
                Id = parameter.Id,
                Kind = parameter.StorageType switch
                {
                    StorageType.String => QueryParameterKind.String,
                    StorageType.Integer => QueryParameterKind.Integer,
                    StorageType.Double => QueryParameterKind.Double,
                    StorageType.ElementId => QueryParameterKind.ElementId,
                    _ => QueryParameterKind.Unknown
                },
                DataType = parameter.Definition?.GetDataType()
            };
        }
    }

    private static QueryParameterDescriptor Create(string name, ElementId id, ForgeTypeId dataType)
    {
        var typeId = dataType.TypeId ?? string.Empty;
        var kind = UnitUtils.IsMeasurableSpec(dataType)
            ? QueryParameterKind.Double
            : typeId.Contains("spec:string", StringComparison.OrdinalIgnoreCase)
                ? QueryParameterKind.String
                : typeId.Contains("yesno", StringComparison.OrdinalIgnoreCase) ||
                  typeId.Contains("int", StringComparison.OrdinalIgnoreCase)
                    ? QueryParameterKind.Integer
                    : QueryParameterKind.ElementId;
        return new QueryParameterDescriptor { Name = name, Id = id, Kind = kind, DataType = dataType };
    }

    private static bool AllResolved(
        IEnumerable<string> requested,
        IDictionary<string, QueryParameterDescriptor> result) =>
        requested.All(name => result.TryGetValue(name, out var descriptor) && descriptor.Kind != QueryParameterKind.Unknown);

    private static ElementId CreateBuiltInId(BuiltInParameter parameter)
    {
#if REVIT2024_OR_GREATER
        return new ElementId((long)parameter);
#else
        return new ElementId((int)parameter);
#endif
    }
}
