namespace RevitModelMcp.Core.Families;

public sealed class ParameterUsageInput(
    string name, bool isShared, bool builtIn, IReadOnlyList<string> associations,
    IReadOnlyList<string> dimensionLabels, IReadOnlyList<string> arrayLabels, string? formula)
{
    public string Name { get; } = name;
    public bool IsShared { get; } = isShared;
    public bool BuiltIn { get; } = builtIn;
    public IReadOnlyList<string> Associations { get; } = associations;
    public IReadOnlyList<string> DimensionLabels { get; } = dimensionLabels;
    public IReadOnlyList<string> ArrayLabels { get; } = arrayLabels;
    public string? Formula { get; } = formula;
}

public sealed class ParameterUsageResult(bool used, bool dataCarrierRisk, IReadOnlyList<string> usedBy)
{
    public bool Used { get; } = used;
    public bool DataCarrierRisk { get; } = dataCarrierRisk;
    public IReadOnlyList<string> UsedBy { get; } = usedBy;
}

public static class ParameterUsage
{
    public static IReadOnlyDictionary<string, ParameterUsageResult> Evaluate(IReadOnlyList<ParameterUsageInput> parameters)
    {
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        var result = new Dictionary<string, ParameterUsageResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            var reasons = new List<string>();
            if (parameter.Associations.Count > 0) reasons.Add("association");
            if (parameter.DimensionLabels.Count > 0 || parameter.ArrayLabels.Count > 0) reasons.Add("label");
            reasons.AddRange(parameters
                .Where(other => !ReferenceEquals(other, parameter) &&
                    other.Formula?.IndexOf(parameter.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(other => "formula:" + other.Name));
            var used = parameter.BuiltIn || reasons.Count > 0;
            result[parameter.Name] = new ParameterUsageResult(used, !used && parameter.IsShared, reasons);
        }
        return result;
    }
}
