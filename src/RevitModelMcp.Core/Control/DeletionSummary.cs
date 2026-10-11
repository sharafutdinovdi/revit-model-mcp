namespace RevitModelMcp.Core.Control;

/// <summary>Reports deletions honestly: Revit removes more elements at commit than Delete returns.</summary>
public static class DeletionSummary
{
    /// <summary>IDs present before the deletion, absent after the commit and not already reported, in ascending order.</summary>
    public static List<long> ExtraRemoved(IEnumerable<long> before, IEnumerable<long> after, IEnumerable<long> reported)
    {
        var remaining = new HashSet<long>(after);
        var known = new HashSet<long>(reported);
        return before.Where(id => !remaining.Contains(id) && !known.Contains(id)).Distinct().OrderBy(id => id).ToList();
    }

    public static string PreviewText(int deletedCount, int requested, int dependents, string documentTitle) =>
        $"Delete at least {deletedCount} elements ({requested} requested, {dependents} dependents) from {documentTitle}. Revit may remove more at commit (groups, dimensions, constraints). One undo entry restores them.";

    public static string ExtraWarning(int extra) =>
        $"Revit also removed {extra} {(extra == 1 ? "element" : "elements")} at commit (for example empty groups or dimensions).";
}
