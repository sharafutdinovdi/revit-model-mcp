using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Serialization;

public sealed class SheetViewPlacementTests
{
    [Test]
    [Arguments(false, false, true, true)]
    [Arguments(true, false, true, false)]
    [Arguments(false, true, true, false)]
    [Arguments(false, false, false, false)]
    public async Task IsUnplaced_RequiresAnEligibleNonTemplateViewWithoutPlacements(
        bool isTemplate, bool isPlaced, bool canPlace, bool expected)
    {
        const long viewId = 42;
        HashSet<long> placedViewIds = isPlaced ? [viewId] : [99];

        var unplaced = SheetViewPlacement.IsUnplaced(canPlace, viewId, isTemplate, placedViewIds, eligible => eligible);

        await Assert.That(unplaced).IsEqualTo(expected);
    }
}
