using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class DeletionSummaryTests
{
    [Test]
    public async Task ExtraRemoved_ReturnsOnlyUnreportedVanishedIds()
    {
        var extra = DeletionSummary.ExtraRemoved([1, 2, 3, 4, 5, 6], [1, 6], [2, 3]);
        await Assert.That(extra).IsEquivalentTo(new List<long> { 4, 5 });
    }

    [Test]
    public async Task ExtraRemoved_NothingExtra_IsEmpty()
    {
        await Assert.That(DeletionSummary.ExtraRemoved([1, 2], [1], [2])).IsEmpty();
    }

    [Test]
    public async Task ExtraRemoved_IgnoresElementsCreatedAfterTheSnapshot()
    {
        await Assert.That(DeletionSummary.ExtraRemoved([1], [1, 9], [])).IsEmpty();
    }

    [Test]
    public async Task PreviewText_SaysAtLeastAndMentionsCounts()
    {
        var text = DeletionSummary.PreviewText(600, 560, 40, "Model");
        await Assert.That(text).Contains("at least 600");
        await Assert.That(text).Contains("560 requested");
        await Assert.That(text).Contains("Revit may remove more");
    }

    [Test]
    [Arguments(1, "1 element at commit")]
    [Arguments(66, "66 elements at commit")]
    public async Task ExtraWarning_UsesPlural(int extra, string expected) =>
        await Assert.That(DeletionSummary.ExtraWarning(extra)).Contains(expected);
}
