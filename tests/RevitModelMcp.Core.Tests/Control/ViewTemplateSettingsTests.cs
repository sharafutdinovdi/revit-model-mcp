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
        var templateParameters = new HashSet<string>();
        if (scaleControlled) templateParameters.Add("VIEW_SCALE");
        if (displayStyleControlled) templateParameters.Add("MODEL_GRAPHICS_STYLE");
        if (detailLevelControlled) templateParameters.Add("VIEW_DETAIL_LEVEL");
        var settings = ViewTemplateSettings.GetSkipped(
            templateParameters, new HashSet<string>(),
            scaleRequested, displayStyleRequested, detailLevelRequested);

        await Assert.That(string.Join(",", settings)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("GRAPHIC_DISPLAY_OPTIONS_MODEL", "displayStyle")]
    [Arguments("MODEL_GRAPHICS_STYLE_ANON_DRAFT", "displayStyle")]
    [Arguments("MODEL_GRAPHICS_STYLE", "displayStyle")]
    [Arguments("VIEW_SCALE", "scale")]
    [Arguments("VIEW_SCALE_PULLDOWN_IMPERIAL", "scale")]
    [Arguments("VIEW_SCALE_PULLDOWN_METRIC", "scale")]
    [Arguments("VIEW_DETAIL_LEVEL", "detailLevel")]
    public async Task GetSkipped_MapsEachTemplateParameter(string parameter, string expected)
    {
        var templateParameters = new HashSet<string> { parameter };
        await Assert.That(string.Join(",", ViewTemplateSettings.GetSkipped(
            templateParameters, new HashSet<string>(), true, true, true))).IsEqualTo(expected);
        await Assert.That(ViewTemplateSettings.GetSkipped(
            templateParameters, new HashSet<string> { parameter }, true, true, true)).IsEmpty();
        await Assert.That(ViewTemplateSettings.GetSkipped(
            templateParameters, new HashSet<string>(), false, false, false)).IsEmpty();
    }

    [Test]
    public async Task GetSkipped_EmptyTemplateControlsNothing()
    {
        await Assert.That(ViewTemplateSettings.GetSkipped(
            new HashSet<string>(), new HashSet<string>(), true, true, true)).IsEmpty();
    }

    [Test]
    public async Task GetSkipped_NonControlledParameterDoesNotSuppressAnotherControlledParameter()
    {
        await Assert.That(string.Join(",", ViewTemplateSettings.GetSkipped(
            new HashSet<string> { "VIEW_SCALE", "VIEW_SCALE_PULLDOWN_METRIC" },
            new HashSet<string> { "VIEW_SCALE" }, true, true, true))).IsEqualTo("scale");
    }
}
