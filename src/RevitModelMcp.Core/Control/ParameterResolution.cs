using System.Globalization;
using System.Text.RegularExpressions;
using RevitModelMcp.Core.Naming;

namespace RevitModelMcp.Core.Control;

public sealed record ParameterCandidate(string Id, string Name, string StorageType, string Owner, string Kind, string? BuiltInName,
    long? ParameterElementId = null);

public static class ParameterResolution
{
    private static readonly Regex BuiltInNamePattern = new("^[A-Za-z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$", RegexOptions.Compiled);

    public static void ValidateIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) ||
            !Guid.TryParse(identifier, out _) &&
            !(long.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) &&
            !BuiltInNamePattern.IsMatch(identifier))
            throw new ArgumentException("parameterId must be a BuiltInParameter name, shared parameter GUID or positive ParameterElement ID.");
    }

    public static void ValidateJsonValue(string parameter, object value)
    {
        if (value is string or int or long) return;
        if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number)) return;
        if (value is float single && !float.IsNaN(single) && !float.IsInfinity(single)) return;
        if (value is decimal) return;
        throw new ArgumentException($"Parameter '{parameter}' value must be a JSON string, integer or finite number.");
    }

    public static ParameterCandidate Resolve(string parameter, string? parameterId, IEnumerable<ParameterCandidate> candidates)
    {
        if (parameter is null) throw new ArgumentNullException(nameof(parameter));
        if (parameterId is not null) ValidateIdentifier(parameterId);
        var aliases = ParameterNames.BuiltInCandidates(parameter);
        var matches = candidates.Where(candidate => parameterId is not null
                ? SameIdentifier(candidate.Id, parameterId) ||
                  candidate.ParameterElementId is > 0 &&
                  SameIdentifier(candidate.ParameterElementId.Value.ToString(CultureInfo.InvariantCulture), parameterId)
                : string.Equals(candidate.Name, parameter, StringComparison.OrdinalIgnoreCase) ||
                  candidate.BuiltInName is not null && aliases.Contains(candidate.BuiltInName, StringComparer.OrdinalIgnoreCase))
            .GroupBy(candidate => (candidate.Owner, candidate.Id)).Select(group => group.First())
            .OrderBy(candidate => candidate.Owner, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToList();
        var requested = parameterId is null ? $"Parameter '{parameter}'" : $"Parameter id '{parameterId}'";
        if (matches.Count == 0) throw new ArgumentException($"{requested} was not found on the instance or type.");
        if (matches.Count > 1)
            throw new ArgumentException($"{requested} is ambiguous: " + string.Join("; ", matches.Select(candidate =>
                $"id={candidate.Id}, name={candidate.Name}, storageType={candidate.StorageType}, owner={candidate.Owner}, kind={candidate.Kind}")));
        return matches[0];
    }

    public static object ValidateValue(string parameter, string storageType, object value)
    {
        ValidateJsonValue(parameter, value);
        if (storageType == "String" && value is string text) return text;
        if (storageType == "Integer" && value is int integer) return integer;
        if (storageType == "Integer" && value is long wide && wide is >= int.MinValue and <= int.MaxValue) return (int)wide;
        if (storageType == "Double" && value is int or long or double or float or decimal)
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsNaN(number) && !double.IsInfinity(number)) return number;
        }
        if (storageType is not ("String" or "Integer" or "Double"))
            throw new ArgumentException($"Parameter '{parameter}' has unsupported storage type {storageType}; ElementId parameters cannot be set.");
        throw new ArgumentException($"Parameter '{parameter}' expects {storageType} storage type value.");
    }

    private static bool SameIdentifier(string left, string right)
    {
        if (Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid))
            return leftGuid == rightGuid;
        if (long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftId) &&
            long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightId))
            return leftId == rightId;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
