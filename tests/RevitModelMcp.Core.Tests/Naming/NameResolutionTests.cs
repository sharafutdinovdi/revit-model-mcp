using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Naming;

namespace RevitModelMcp.Core.Tests.Naming;

public sealed class NameResolutionTests
{
    private static readonly string[] RussianColumns = ["Несущие колонны"];

    [Test]
    [Arguments("OST_StructuralColumns")]
    [Arguments("ost_structuralcolumns")]
    [Arguments("StructuralColumns")]
    [Arguments("Structural Columns")]
    [Arguments("structural columns")]
    [Arguments("Несущие колонны")]
    public async Task CategoryMatches_AcceptsAllThreeForms(string requested)
    {
        await Assert.That(CategoryNames.Matches(requested, RussianColumns, "OST_StructuralColumns")).IsTrue();
    }

    [Test]
    public async Task CategoryMatches_RejectsOtherCategory()
    {
        await Assert.That(CategoryNames.Matches("Columns", RussianColumns, "OST_StructuralColumns")).IsFalse();
        await Assert.That(CategoryNames.Matches("Walls", RussianColumns, null)).IsFalse();
    }

    [Test]
    [Arguments("OST_StructuralColumns", "Structural Columns")]
    [Arguments("OST_MEPSpaces", "Spaces")]
    [Arguments("OST_PipeCurves", "Pipes")]
    [Arguments("OST_GenericModel", "Generic Models")]
    [Arguments("OST_LightingFixtures", "Lighting Fixtures")]
    [Arguments("OST_Walls", "Walls")]
    public async Task EnglishLabel_DerivesFromEnumName(string builtIn, string expected)
    {
        await Assert.That(CategoryNames.EnglishLabel(builtIn)).IsEqualTo(expected);
    }

    [Test]
    public async Task CategoryMatches_EnglishOverrideDiffersFromEnumName()
    {
        await Assert.That(CategoryNames.Matches("Pipes", ["Трубы"], "OST_PipeCurves")).IsTrue();
        await Assert.That(CategoryNames.Matches("Generic Models", ["Обобщенные модели"], "OST_GenericModel")).IsTrue();
    }

    [Test]
    public async Task Forms_FeedCloseMatchesFromEveryForm()
    {
        var forms = CategoryNames.Forms(RussianColumns, "OST_StructuralColumns").ToList();
        await Assert.That(forms).IsEquivalentTo(new[] { "Несущие колонны", "OST_StructuralColumns", "Structural Columns" });
        var matches = ActionJobParser.ClosestFamilyNames("Structural Column", forms);
        await Assert.That(matches).Contains("Structural Columns");
    }

    [Test]
    [Arguments("Comments", "ALL_MODEL_INSTANCE_COMMENTS")]
    [Arguments("comments", "ALL_MODEL_INSTANCE_COMMENTS")]
    [Arguments("Mark", "ALL_MODEL_MARK")]
    [Arguments("Type Mark", "ALL_MODEL_TYPE_MARK")]
    [Arguments("Description", "ALL_MODEL_DESCRIPTION")]
    [Arguments("Level", "FAMILY_LEVEL_PARAM")]
    [Arguments("Offset", "INSTANCE_FREE_HOST_OFFSET_PARAM")]
    public async Task ParameterCandidates_MapEnglishLabels(string requested, string first)
    {
        await Assert.That(ParameterNames.BuiltInCandidates(requested).First()).IsEqualTo(first);
    }

    [Test]
    public async Task ParameterCandidates_AcceptEnumNames()
    {
        await Assert.That(ParameterNames.BuiltInCandidates("ALL_MODEL_INSTANCE_COMMENTS"))
            .IsEquivalentTo(new[] { "ALL_MODEL_INSTANCE_COMMENTS" });
        await Assert.That(ParameterNames.BuiltInCandidates("all_model_mark"))
            .IsEquivalentTo(new[] { "ALL_MODEL_MARK" });
    }

    [Test]
    public async Task ParameterCandidates_UnknownLocalizedName_IsEmpty()
    {
        await Assert.That(ParameterNames.BuiltInCandidates("Комментарии")).IsEmpty();
        await Assert.That(ParameterNames.BuiltInCandidates("Fire Rating")).IsEmpty();
    }
}
