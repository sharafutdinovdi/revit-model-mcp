using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ViewTemplateSettingsTests
{
    [Test]
    [Arguments(false, false, false, false, false, false, "")]
    [Arguments(true, false, true, false, true, false, "")]
    [Arguments(true, true, true, false, true, false, "scale")]
    [Arguments(true, false, true, true, true, false, "displayStyle")]
    [Arguments(true, false, true, false, true, true, "detailLevel")]
    [Arguments(true, true, true, true, true, true, "scale,displayStyle,detailLevel")]
    [Arguments(false, true, false, true, false, true, "")]
    [Arguments(false, true, true, true, true, true, "displayStyle,detailLevel")]
    [Arguments(true, true, false, true, true, true, "scale,detailLevel")]
    [Arguments(true, true, true, true, false, true, "scale,displayStyle")]
    public async Task GetSkipped_ListsOnlyRequestedControlledSettingsInOrder(
        bool scaleRequested, bool scaleControlled,
        bool displayStyleRequested, bool displayStyleControlled,
        bool detailLevelRequested, bool detailLevelControlled, string expected)
    {
        var settings = ViewTemplateSettings.GetSkipped(
            scaleRequested, scaleControlled,
            displayStyleRequested, displayStyleControlled,
            detailLevelRequested, detailLevelControlled);

        await Assert.That(string.Join(",", settings)).IsEqualTo(expected);
    }
}
