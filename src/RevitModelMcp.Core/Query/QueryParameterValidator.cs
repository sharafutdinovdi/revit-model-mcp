using System.Globalization;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Naming;

namespace RevitModelMcp.Core.Query;

public static class QueryParameterValidator
{
    private static readonly HashSet<string> BuiltInFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "category", "family", "type", "name", "level", "workset", "phase", "areaScheme", "hasWarnings", "pinned"
    };

    public static bool IsBuiltInField(string name) => BuiltInFields.Contains(name);

    public static bool MatchesPinned(bool pinned, ParameterFilterSpec filter) => filter.Operator switch
    {
        ParameterOperator.Exists or ParameterOperator.NotEmpty => true,
        ParameterOperator.Empty => false,
        ParameterOperator.Equals when bool.TryParse(filter.Value, out var value) => pinned == value,
        _ => throw new ArgumentException("Field 'pinned' supports equals with true or false, exists, empty and not-empty.")
    };

    public static void ValidateAggregateFields(
        IEnumerable<string> groupBy,
        string? numericField,
        IEnumerable<string> available)
    {
        var availableFields = available.ToList();
        foreach (var field in groupBy)
        {
            Resolve(field, availableFields);
        }

        if (!string.IsNullOrWhiteSpace(numericField))
        {
            Resolve(numericField!, availableFields);
        }
    }

    public static void ValidateFilterResolution(
        string requested, bool hasCandidates, bool resolved, Func<IEnumerable<string>> modelWideNames)
    {
        if (resolved) return;
        Resolve(requested, modelWideNames());
        if (hasCandidates)
            throw new ArgumentException(
                $"Parameter '{requested}' was not found on the queried elements or their types. Use list-catalog with section=parameters to see available names.",
                nameof(requested));
    }

    public static string Resolve(string requested, IEnumerable<string> available)
    {
        if (IsBuiltInField(requested)) return BuiltInFields.First(name =>
            string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        var match = available.FirstOrDefault(name =>
            string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ArgumentException(
                $"Parameter '{requested}' was not found. Use list-catalog with section=parameters to see available names.",
                nameof(requested));
        }

        return match;
    }
}

public static class CategoryNameResolver
{
    public static T Resolve<T>(
        string requested,
        IEnumerable<T> available,
        Func<T, IEnumerable<string>> revitNames,
        Func<T, string?> builtInName)
    {
        foreach (var candidate in available)
        {
            if (CategoryNames.Matches(requested, revitNames(candidate), builtInName(candidate)))
            {
                return candidate;
            }
        }

        throw new ArgumentException(
            $"Category '{requested}' was not found. Use list-catalog with section=categories to see available names.",
            nameof(requested));
    }
}

public enum QueryParameterKind
{
    Unknown,
    String,
    Integer,
    Double,
    ElementId
}

public static class QueryParameterSemantics
{
    public static double ParseNumericFilterValue(string value, string parameter) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Value '{value}' for parameter '{parameter}' is not a number.");

    public static T? ResolveValueSource<T>(T? instanceParameter, Func<T?> typeParameter) where T : class
    {
        return instanceParameter ?? typeParameter();
    }

    public static bool IsDefinedAlias(string requested, ISet<string> definedNames) =>
        definedNames.Contains(requested);

    public static bool MatchesParameterName(
        string requested, string? name, long id, long? builtInId) =>
        string.Equals(name, requested, StringComparison.OrdinalIgnoreCase) || id == builtInId;

    public static IReadOnlyList<T> ResolveNamedSources<T>(
        IEnumerable<T> instanceParameters, Func<IEnumerable<T>> typeParameters,
        Func<T, bool> matches)
    {
        var instanceMatches = instanceParameters.Where(matches).ToArray();
        return instanceMatches.Length > 0 ? instanceMatches : typeParameters().Where(matches).ToArray();
    }

    public static T CombineFilterAlternatives<T>(IReadOnlyList<T> rules, Func<IReadOnlyList<T>, T> combineAny) =>
        rules.Count switch
        {
            0 => throw new ArgumentException("At least one resolved parameter rule is required.", nameof(rules)),
            1 => rules[0],
            _ => combineAny(rules)
        };

    public static bool MatchesValueState(IEnumerable<bool?> values, ParameterOperator operation) =>
        values.Any(value => value.HasValue &&
            (operation == ParameterOperator.Exists || value.Value == (operation == ParameterOperator.NotEmpty)));

    public static bool HasValue(bool hasValue, string storageType, string? text = null, long? elementId = null)
    {
        if (!hasValue) return false;
        return storageType switch
        {
            "String" => !string.IsNullOrWhiteSpace(text),
            "ElementId" => elementId is not null && elementId != -1,
            "Integer" or "Double" => true,
            _ => false
        };
    }

    public static QueryParameterKind ResolveKind(string? dataType, bool measurable, string? storageType = null)
    {
        if (storageType is not null)
        {
            return storageType switch
            {
                "String" => QueryParameterKind.String,
                "Integer" => QueryParameterKind.Integer,
                "Double" => QueryParameterKind.Double,
                "ElementId" => QueryParameterKind.ElementId,
                _ => QueryParameterKind.Unknown
            };
        }

        if (measurable) return QueryParameterKind.Double;
        if (dataType is null) return QueryParameterKind.Unknown;
        if (dataType.IndexOf("spec:string", StringComparison.OrdinalIgnoreCase) >= 0 ||
            dataType.IndexOf("spec.string", StringComparison.OrdinalIgnoreCase) >= 0)
            return QueryParameterKind.String;
        if (dataType.IndexOf("yesno", StringComparison.OrdinalIgnoreCase) >= 0 ||
            dataType.IndexOf("int", StringComparison.OrdinalIgnoreCase) >= 0)
            return QueryParameterKind.Integer;
        return QueryParameterKind.Unknown;
    }
}
