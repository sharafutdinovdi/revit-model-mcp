namespace RevitModelMcp.Core.Models;

public static class UnplacedViewRule
{
    public static bool IsUnplaced<TView>(TView view, long viewId, bool isTemplate, ISet<long> placedViewIds,
        Func<TView, bool> canPlace) =>
        !isTemplate && !placedViewIds.Contains(viewId) && canPlace(view);
}
