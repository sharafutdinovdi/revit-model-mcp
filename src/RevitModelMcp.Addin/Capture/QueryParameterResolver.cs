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
    public static Dictionary<string, QueryParameterDescriptor> Resolve(
        Document document,
        IEnumerable<string> requestedNames,
        Func<FilteredElementCollector> candidateCollector)
    {
        var requested = requestedNames.Where(name => !ElementFieldReader.IsBuiltInField(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
        foreach (var parameterElement in new FilteredElementCollector(document)
                     .OfClass(typeof(ParameterElement))
                     .Cast<ParameterElement>())
        {
            var definition = parameterElement.GetDefinition();
            if (definition is null || !requested.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
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
        foreach (BuiltInParameter builtIn in Enum.GetValues(typeof(BuiltInParameter)))
        {
            var enumName = builtIn.ToString();
            string? label = null;
            try
            {
                label = LabelUtils.GetLabelFor(builtIn);
            }
            catch
            {
                // Some internal built-in parameters have no display label.
            }

            var requestedName = requested.FirstOrDefault(name =>
                string.Equals(name, enumName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, label, StringComparison.OrdinalIgnoreCase));
            if (requestedName is null || result.ContainsKey(requestedName))
            {
                continue;
            }

            result[requestedName] = new QueryParameterDescriptor
            {
                Name = string.IsNullOrWhiteSpace(label) ? enumName : label!,
                Id = CreateBuiltInId(builtIn)
            };
        }
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
        foreach (Parameter parameter in element.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (name is null || !requested.Contains(name, StringComparer.OrdinalIgnoreCase))
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
