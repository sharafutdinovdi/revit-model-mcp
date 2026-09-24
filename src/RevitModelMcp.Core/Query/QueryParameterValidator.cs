using RevitModelMcp.Core.Naming;

namespace RevitModelMcp.Core.Query;

public static class QueryParameterValidator
{
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
