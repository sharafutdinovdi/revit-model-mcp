namespace RevitModelMcp.Core.Control;

/// <summary>Decides whether a view can be placed on a sheet from its outline size.</summary>
public static class SheetPlacementPolicy
{
    /// <summary>A view whose outline is missing or has no area (for example an empty drafting view) has nothing to show.</summary>
    public static bool HasVisibleContent(double? width, double? height) =>
        width is > 0 and < double.PositiveInfinity && height is > 0 and < double.PositiveInfinity;

    public static string EmptyViewMessage(string viewName) => $"View '{viewName}' has no visible content to place.";
}
