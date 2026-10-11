using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class SheetPlacementPolicyTests
{
    [Test]
    [Arguments(null, null, false)]
    [Arguments(0.0, 0.0, false)]
    [Arguments(0.0, 2.0, false)]
    [Arguments(2.0, 0.0, false)]
    [Arguments(-1.0, 2.0, false)]
    [Arguments(double.NaN, 2.0, false)]
    [Arguments(1.5, 2.0, true)]
    public async Task HasVisibleContent_RequiresPositiveArea(double? width, double? height, bool expected) =>
        await Assert.That(SheetPlacementPolicy.HasVisibleContent(width, height)).IsEqualTo(expected);

    [Test]
    public async Task EmptyViewMessage_NamesTheView() =>
        await Assert.That(SheetPlacementPolicy.EmptyViewMessage("N11 empty draft")).IsEqualTo("View 'N11 empty draft' has no visible content to place.");
}
