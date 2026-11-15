using Autodesk.Revit.DB;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Capture;

internal sealed class QueryParameterDescriptor
{
    public string Name { get; set; } = string.Empty;
    public ElementId Id { get; set; } = ElementId.InvalidElementId;
    public QueryParameterKind Kind { get; set; }
    public ForgeTypeId? DataType { get; set; }
    public bool HasTypeSource { get; set; }
    public Definition? Definition { get; set; }
}

internal static class QueryParameterResolver
{
    private static readonly Lazy<(Dictionary<string, BuiltInParameter> Names,
        Dictionary<BuiltInParameter, (string Name, int Order)> Details)> BuiltIns = new(CreateBuiltInCache);
    private static readonly HashSet<string> BuiltInAliasNames =
        new(Enum.GetNames(typeof(BuiltInParameter)), StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, IReadOnlyList<QueryParameterDescriptor>> ResolveFilters(
        Document document, IEnumerable<string> requestedNames,
        Func<FilteredElementCollector> candidateCollector)
    {
        var requested = requestedNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = requested.ToDictionary(name => name,
            _ => new Dictionary<ElementId, QueryParameterDescriptor>(), StringComparer.OrdinalIgnoreCase);
        if (requested.Length == 0)
            return new Dictionary<string, IReadOnlyList<QueryParameterDescriptor>>(StringComparer.OrdinalIgnoreCase);
        var builtInIds = requested.ToDictionary(name => name,
            name => QueryParameterSemantics.IsDefinedAlias(name, BuiltInAliasNames) &&
                    Enum.TryParse<BuiltInParameter>(name, true, out var builtIn) ? (long?)builtIn : null,
            StringComparer.OrdinalIgnoreCase);
        var typeElements = new Dictionary<ElementId, Element?>();
        var typeParameters = new Dictionary<(ElementId TypeId, string Name, bool IgnoreCase), IList<Parameter>>();
        var hasCandidates = false;
        void GatherBindings(IReadOnlyList<string> names, bool ignoreCase)
        {
            using var collector = candidateCollector();
            foreach (var element in collector)
            {
                hasCandidates = true;
                ElementId? typeId = null;
                IList<Parameter> ReadTypeParameters(string name)
                {
                    typeId ??= element.GetTypeId();
                    var key = (typeId, name, ignoreCase);
                    if (typeParameters.TryGetValue(key, out var parameters)) return parameters;
                    if (!typeElements.TryGetValue(typeId, out var type))
                    {
                        type = document.GetElement(typeId);
                        typeElements.Add(typeId, type);
                    }
                    parameters = type is null ? Array.Empty<Parameter>()
                        : ReadFilterParameters(type, name, builtInIds[name], ignoreCase);
                    typeParameters.Add(key, parameters);
                    return parameters;
                }
                foreach (var name in names)
                {
                    var fromType = false;
                    IEnumerable<Parameter> sources;
                    if (ignoreCase)
                    {
                        sources = QueryParameterSemantics.ResolveNamedSources(
                            element.Parameters.Cast<Parameter>(), () =>
                            {
                                fromType = true;
                                return ReadTypeParameters(name);
                            },
                            parameter => QueryParameterSemantics.MatchesParameterName(
                                name, parameter.Definition?.Name, RevitValueReader.GetId(parameter.Id), builtInIds[name]));
                    }
                    else
                    {
                        var instanceParameters = ReadFilterParameters(element, name, builtInIds[name], false);
                        fromType = instanceParameters.Count == 0;
                        sources = fromType ? ReadTypeParameters(name) : instanceParameters;
                    }
                    foreach (var parameter in sources)
                    {
                        if (candidates[name].TryGetValue(parameter.Id, out var existing))
                        {
                            existing.HasTypeSource |= fromType;
                            continue;
                        }
                        var definition = parameter.Definition;
                        candidates[name].Add(parameter.Id, new QueryParameterDescriptor
                        {
                            Name = name,
                            Id = parameter.Id,
                            Kind = QueryParameterSemantics.ResolveKind(null, false, parameter.StorageType.ToString()),
                            DataType = definition.GetDataType(),
                            HasTypeSource = fromType,
                            Definition = definition
                        });
                    }
                }
            }
        }
        GatherBindings(requested, false);
        // Only names without any fast-pass binding receive a case-insensitive enumeration pass.
        var unresolvedNames = QueryParameterSemantics.GetUnresolvedFilterNames(requested, name => candidates[name].Count > 0);
        if (hasCandidates && unresolvedNames.Count > 0) GatherBindings(unresolvedNames, true);
        foreach (var name in requested)
            QueryParameterValidator.ValidateFilterResolution(
                name, hasCandidates, candidates[name].Count > 0,
                () => Resolve(document, [name], candidateCollector).Keys);
        return candidates.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<QueryParameterDescriptor>)pair.Value.Values.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static IList<Parameter> ReadFilterParameters(
        Element element, string name, long? builtInId, bool ignoreCase)
    {
        if (ignoreCase)
            return element.Parameters.Cast<Parameter>().Where(parameter =>
                QueryParameterSemantics.MatchesParameterName(
                    name, parameter.Definition?.Name, RevitValueReader.GetId(parameter.Id), builtInId)).ToArray();
        if (!builtInId.HasValue) return element.GetParameters(name);
        var parameter = element.get_Parameter((BuiltInParameter)builtInId.Value);
        return parameter is null ? Array.Empty<Parameter>() : new[] { parameter };
    }

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
                PluginLog.Debug($"Skipped parameter {enumName} display label.", exception);
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
                Kind = QueryParameterSemantics.ResolveKind(null, false, parameter.StorageType.ToString()),
                DataType = parameter.Definition?.GetDataType()
            };
        }
    }

    private static QueryParameterDescriptor Create(string name, ElementId id, ForgeTypeId dataType)
    {
        var kind = QueryParameterSemantics.ResolveKind(dataType.TypeId, UnitUtils.IsMeasurableSpec(dataType));
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
