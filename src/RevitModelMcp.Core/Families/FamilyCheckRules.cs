namespace RevitModelMcp.Core.Families;

public static class FamilyCheckRules
{
    public static (IReadOnlyList<T> Assessed, IReadOnlyList<T> Skipped) SelectFamilies<T>(IEnumerable<T> families)
    {
        var selected = families.ToList();
        return (selected.Take(20).ToList(), selected.Skip(20).ToList());
    }

    public static string Severity(string id) => id switch
    {
        "regen_failed" or "geometry_empty" or "geometry_invalid" or "parameter_no_effect" or
            "expected_mismatch" or "place_failed" or "host_not_cut" => "error",
        "imported_cad" or "overlap_high" => "warning",
        _ => "info"
    };

    public static bool SameGeometry(FamilyCheckBounds before, double beforeVolume, FamilyCheckBounds after, double afterVolume) =>
        Math.Abs(before.Width - after.Width) <= 0.01 && Math.Abs(before.Depth - after.Depth) <= 0.01 &&
        Math.Abs(before.Height - after.Height) <= 0.01 && Math.Abs(beforeVolume - afterVolume) <= 1e-9;

    public static string? NoEffectSeverity(FamilyCheckBounds before, double beforeVolume, FamilyCheckBounds after,
        double afterVolume, bool labelled, bool reporting, bool ambiguous) =>
        reporting || !labelled || !SameGeometry(before, beforeVolume, after, afterVolume) ? null : ambiguous ? "info" : "error";

    public static bool ExpectedMatches(FamilyCheckBounds actual, FamilyCheckExpected expected) =>
        (!expected.WidthMm.HasValue || Math.Abs(actual.Width - expected.WidthMm.Value) <= expected.ToleranceMm + 1e-9) &&
        (!expected.DepthMm.HasValue || Math.Abs(actual.Depth - expected.DepthMm.Value) <= expected.ToleranceMm + 1e-9) &&
        (!expected.HeightMm.HasValue || Math.Abs(actual.Height - expected.HeightMm.Value) <= expected.ToleranceMm + 1e-9);

    // Maximum category P90 across corpus groups with at least four complete flex families.
    // Count belongs to the group supplying the selected P90. Missing categories are not assessed.
    public static (double P90, int Count)? OverlapThreshold(string category) => category switch
    {
        "OST_Casework" => (0.03367, 7),
        "OST_Doors" => (0.4418, 16),
        "OST_Furniture" => (0.05211, 15),
        "OST_GenericModel" => (0.00674, 8),
        "OST_LightingFixtures" => (0.01481, 4),
        "OST_StructuralColumns" => (0.329, 4),
        "OST_Windows" => (0.1107, 17),
        _ => null
    };

    public static string Status(IEnumerable<FamilyCheckFinding> findings, bool assessed) =>
        findings.Any(finding => finding.Severity == "error") ? "failed" : assessed ? "passed" : "not_assessed";
}
