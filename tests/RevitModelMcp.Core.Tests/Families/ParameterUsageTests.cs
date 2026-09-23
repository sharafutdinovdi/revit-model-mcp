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
