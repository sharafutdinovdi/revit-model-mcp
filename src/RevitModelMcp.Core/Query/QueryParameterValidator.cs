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
    public static T? ResolveValueSource<T>(T? instanceParameter, Func<T?> typeParameter) where T : class
    {
        return instanceParameter ?? typeParameter();
    }

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
