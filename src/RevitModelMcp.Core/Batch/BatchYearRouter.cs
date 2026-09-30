using System.Text.RegularExpressions;

namespace RevitModelMcp.Core.Batch;

public sealed record BatchYearDecision(int SavedYear, int? RuntimeYear, bool UpgradedInMemory, string? Error);

public static class BatchYearRouter
{
    public static int Parse(string format)
    {
        var match = Regex.Match(format ?? string.Empty, @"(?<!\d)20(2[2-7])(?!\d)");
        if (!match.Success || !int.TryParse(match.Value, out var year))
            throw new ArgumentException("Saved Revit year is unavailable or unsupported.", nameof(format));
        return year;
    }

    public static BatchYearDecision Route(int savedYear, IEnumerable<int> installed, IEnumerable<int>? allowed = null)
    {
        if (savedYear is < 2022 or > 2027) return new(savedYear, null, false, "Unsupported saved Revit year.");
        var allowedYears = allowed?.ToHashSet();
        var selected = installed.Where(year => year is >= 2022 and <= 2027 && year >= savedYear &&
            (allowedYears is null || allowedYears.Contains(year))).Distinct().Order().FirstOrDefault();
        return selected == 0
            ? new(savedYear, null, false, "No allowed installed Revit year can open this model without downgrading.")
            : new(savedYear, selected, selected > savedYear, null);
    }

    public static IReadOnlyDictionary<int, IReadOnlyList<BatchModel>> GroupByRuntimeYear(IEnumerable<BatchModel> models) =>
        models.Where(model => model.RuntimeYear.HasValue)
            .GroupBy(model => model.RuntimeYear!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<BatchModel>)group.ToArray());
}
