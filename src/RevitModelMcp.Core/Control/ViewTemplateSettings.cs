namespace RevitModelMcp.Core.Control;

public static class ViewTemplateSettings
{
    public static List<string> GetSkipped(
        ISet<string> templateParameters, ISet<string> nonControlledParameters,
        bool scaleRequested, bool displayStyleRequested, bool detailLevelRequested)
    {
        bool Controls(string parameter) => templateParameters.Contains(parameter) && !nonControlledParameters.Contains(parameter);
        var skipScale = scaleRequested && (Controls("VIEW_SCALE") ||
            Controls("VIEW_SCALE_PULLDOWN_METRIC") || Controls("VIEW_SCALE_PULLDOWN_IMPERIAL"));
        // Parameter names avoid references to enum members unavailable in older Revit APIs.
        var skipDisplayStyle = displayStyleRequested && (Controls("GRAPHIC_DISPLAY_OPTIONS_MODEL") ||
            Controls("MODEL_GRAPHICS_STYLE_ANON_DRAFT") || Controls("MODEL_GRAPHICS_STYLE"));
        var skipDetailLevel = detailLevelRequested && Controls("VIEW_DETAIL_LEVEL");
        var settings = new List<string>((skipScale ? 1 : 0) + (skipDisplayStyle ? 1 : 0) + (skipDetailLevel ? 1 : 0));
        if (skipScale) settings.Add("scale");
        if (skipDisplayStyle) settings.Add("displayStyle");
        if (skipDetailLevel) settings.Add("detailLevel");
        return settings;
    }
}
