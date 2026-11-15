namespace RevitModelMcp.Core.Control;

public static class ViewTemplateSettings
{
    public static List<string> GetSkipped(
        bool scaleRequested, bool scaleControlled,
        bool displayStyleRequested, bool displayStyleControlled,
        bool detailLevelRequested, bool detailLevelControlled)
    {
        var skipScale = scaleRequested && scaleControlled;
        var skipDisplayStyle = displayStyleRequested && displayStyleControlled;
        var skipDetailLevel = detailLevelRequested && detailLevelControlled;
        var settings = new List<string>((skipScale ? 1 : 0) + (skipDisplayStyle ? 1 : 0) + (skipDetailLevel ? 1 : 0));
        if (skipScale) settings.Add("scale");
        if (skipDisplayStyle) settings.Add("displayStyle");
        if (skipDetailLevel) settings.Add("detailLevel");
        return settings;
    }
}
