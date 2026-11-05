using RevitModelMcp.Core.Families;

namespace RevitModelMcp.Core.Tests.Families;

public sealed class ParameterUsageTests
{
    private static ParameterUsageInput Input(string name, bool shared = false, bool builtIn = false,
        string? formula = null, string[]? associations = null, string[]? dimensions = null, string[]? arrays = null) =>
        new(name, shared, builtIn, associations ?? [], dimensions ?? [], arrays ?? [], formula);

    [Test]
    public async Task AssociationMarksParameterUsed()
    {
        var result = ParameterUsage.Evaluate([Input("Width", associations: ["Length"])]);
        await Assert.That(result["Width"].Used).IsTrue();
        await Assert.That(result["Width"].UsedBy).Contains("association");
    }

    [Test]
    public async Task FormulaReferenceMarksParameterUsed()
    {
        var result = ParameterUsage.Evaluate([Input("Width"), Input("Area", formula: "Width * Height")]);
        await Assert.That(result["Width"].UsedBy).Contains("formula:Area");
    }

    [Test]
    public async Task DimensionAndArrayLabelsMarkParametersUsed()
    {
        var result = ParameterUsage.Evaluate([Input("Width", dimensions: ["Width"]), Input("Count", arrays: ["Count"])]);
        await Assert.That(result["Width"].UsedBy).Contains("label");
        await Assert.That(result["Count"].UsedBy).Contains("label");
    }

    [Test]
    public async Task FormulaSubstringIsDeliberatelyOverInclusive()
    {
        var result = ParameterUsage.Evaluate([Input("Width"), Input("Width2"), Input("Area", formula: "Width2 * Height")]);
        await Assert.That(result["Width"].Used).IsTrue();
        await Assert.That(result["Width"].UsedBy).Contains("formula:Area");
    }

    [Test]
    public async Task UnusedSharedHasDataCarrierRiskAndBuiltInUsesSameUsageRule()
    {
        var result = ParameterUsage.Evaluate([Input("Tag", shared: true), Input("BuiltIn", builtIn: true)]);
        await Assert.That(result["Tag"].Used).IsFalse();
        await Assert.That(result["Tag"].DataCarrierRisk).IsTrue();
        await Assert.That(result["BuiltIn"].Used).IsFalse();
        await Assert.That(result["BuiltIn"].UsedBy).IsEmpty();
    }

    [Test]
    public async Task BuiltInWithAssociationIsUsed()
    {
        var result = ParameterUsage.Evaluate([Input("BuiltIn", builtIn: true, associations: ["Length"])]);
        await Assert.That(result["BuiltIn"].Used).IsTrue();
        await Assert.That(result["BuiltIn"].UsedBy).Contains("association");
    }
}

public sealed class FamilyCheckRulesTests
{
    [Test]
    public async Task FamilySelectionCapsAssessmentAndListsEveryRemainder()
    {
        var selected = FamilyCheckRules.SelectFamilies(Enumerable.Range(1, 23));
        await Assert.That(selected.Assessed.Count).IsEqualTo(20);
        await Assert.That(selected.Skipped.SequenceEqual(new[] { 21, 22, 23 })).IsTrue();
    }

    [Test]
    public async Task ParserPreservesExpectedDimensionsFromJson()
    {
        var parsed = RevitModelMcp.Core.Control.ControlJobParser.Parse("""
            {"command":"family-check","families":["Example"],"familyCheck":{"maxTypes":10,"imagePx":800,"flex":true,"hostTest":false,"types":null,"variants":[{"parameter":"Width","values_mm":[950,1050]}],"expected":{"Standard":{"width_mm":1000}},"previews":[],"detailLevels":["fine"]}}
            """);
        await Assert.That(parsed.Error).IsNull();
        await Assert.That(parsed.CoordinatorJob.FamilyCheck!.Expected!["Standard"].WidthMm).IsEqualTo(1000.0);
        await Assert.That(parsed.CoordinatorJob.FamilyCheck.Variants![0].ValuesMm.Count).IsEqualTo(2);
        await Assert.That(parsed.CoordinatorJob.FamilyCheck.Expected["Standard"].ToleranceMm).IsEqualTo(2.0);
    }

    [Test]
    [Arguments("regen_failed", "error")]
    [Arguments("geometry_empty", "error")]
    [Arguments("geometry_invalid", "error")]
    [Arguments("parameter_no_effect", "error")]
    [Arguments("expected_mismatch", "error")]
    [Arguments("place_failed", "error")]
    [Arguments("host_not_cut", "error")]
    [Arguments("imported_cad", "warning")]
    [Arguments("overlap_high", "warning")]
    [Arguments("structure", "info")]
    public async Task ClassifiesSeverity(string id, string expected) =>
        await Assert.That(FamilyCheckRules.Severity(id)).IsEqualTo(expected);

    [Test]
    public async Task ExpectedToleranceIncludesBoundary()
    {
        var bounds = new FamilyCheckBounds { Width = 1002, Depth = 998, Height = 1000 };
        var expected = new FamilyCheckExpected { WidthMm = 1000, DepthMm = 1000, HeightMm = 1000 };
        await Assert.That(FamilyCheckRules.ExpectedMatches(bounds, expected)).IsTrue();
        bounds.Width += 0.001;
        await Assert.That(FamilyCheckRules.ExpectedMatches(bounds, expected)).IsFalse();
    }

    [Test]
    public async Task NoEffectRequiresUnambiguousLabelledDrivingGeometry()
    {
        var before = new FamilyCheckBounds { Width = 1000 };
        var same = new FamilyCheckBounds { Width = 1000.005 };
        await Assert.That(FamilyCheckRules.NoEffectSeverity(before, 1, same, 1, true, false, false)).IsEqualTo("error");
        await Assert.That(FamilyCheckRules.NoEffectSeverity(before, 1, same, 1, true, false, true)).IsEqualTo("info");
        await Assert.That(FamilyCheckRules.NoEffectSeverity(before, 1, same, 1, true, true, false)).IsNull();
        await Assert.That(FamilyCheckRules.NoEffectSeverity(before, 1, same, 1, false, false, false)).IsNull();
        await Assert.That(FamilyCheckRules.NoEffectSeverity(before, 1, same, 1.01, true, false, false)).IsNull();
    }

    [Test]
    [Arguments("OST_Casework", 0.03367, 7)]
    [Arguments("OST_Doors", 0.4418, 16)]
    [Arguments("OST_Furniture", 0.05211, 15)]
    [Arguments("OST_GenericModel", 0.00674, 8)]
    [Arguments("OST_LightingFixtures", 0.01481, 4)]
    [Arguments("OST_StructuralColumns", 0.329, 4)]
    [Arguments("OST_Windows", 0.1107, 17)]
    public async Task OverlapThresholdUsesLargestQualifyingGroup(string category, double expectedP90, int expectedCount)
    {
        var threshold = FamilyCheckRules.OverlapThreshold(category);
        await Assert.That(threshold).IsNotNull();
        await Assert.That(threshold!.Value.P90).IsEqualTo(expectedP90);
        await Assert.That(threshold.Value.Count).IsEqualTo(expectedCount);
    }

    [Test]
    [Arguments(0.4418, false)]
    [Arguments(0.4418000000000001, true)]
    public async Task OverlapWarningRequiresRatioStrictlyAboveThreshold(double ratio, bool expectedWarning)
    {
        var threshold = FamilyCheckRules.OverlapThreshold("OST_Doors");
        await Assert.That(threshold.HasValue && ratio > threshold.Value.P90).IsEqualTo(expectedWarning);
    }

    [Test]
    public async Task OverlapThresholdIgnoresHigherP90FromUndersizedGroup()
    {
        // The open structural-column group has P90 0.4975 from only two families.
        var threshold = FamilyCheckRules.OverlapThreshold("OST_StructuralColumns");
        await Assert.That(threshold).IsEqualTo(((double P90, int Count)?)(0.329, 4));
    }

    [Test]
    [Arguments("unknown")]
    [Arguments("OST_Columns")]
    [Arguments("OST_StairsRailingBaluster")]
    public async Task MissingOverlapCategoryHasNoInventedThreshold(string category) =>
        await Assert.That(FamilyCheckRules.OverlapThreshold(category)).IsNull();

    [Test]
    public async Task WarningsNeverFailAFamily()
    {
        var findings = new[] { new FamilyCheckFinding { Severity = "warning" } };
        await Assert.That(FamilyCheckRules.Status(findings, true)).IsEqualTo("passed");
        await Assert.That(FamilyCheckRules.Status(findings, false)).IsEqualTo("not_assessed");
        findings[0].Severity = "error";
        await Assert.That(FamilyCheckRules.Status(findings, false)).IsEqualTo("failed");
    }

    [Test]
    public async Task ParserRoutesReadOnlyCheckAndPreservesOptions()
    {
        var job = new RevitModelMcp.Core.Control.ControlJobContract { Command = "family-check", Families = ["*"], FamilyCheck = new FamilyCheckOptions() };
        var parsed = RevitModelMcp.Core.Control.ControlJobParseResult.FromContract(job);
        await Assert.That(parsed.Kind).IsEqualTo(RevitModelMcp.Core.Control.ControlJobKind.FamilyCheck);
        await Assert.That(parsed.CoordinatorJob.FamilyCheck!.MaxTypes).IsEqualTo(10);
        var rejected = false;
        try { RevitModelMcp.Core.Control.ActionJobParser.ValidateFamilyMode(parsed.Action!, true); }
        catch (ArgumentException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    [Arguments(0, "3d")]
    [Arguments(101, "3d")]
    [Arguments(10, "perspective")]
    public async Task ParserRejectsInvalidOptions(int maxTypes, string preview)
    {
        var parsed = RevitModelMcp.Core.Control.ControlJobParseResult.FromContract(new()
        {
            Command = "family-check",
            FamilyCheck = new() { MaxTypes = maxTypes, Previews = [preview] }
        });
        await Assert.That(parsed.Error).IsNotNull();
    }
}
