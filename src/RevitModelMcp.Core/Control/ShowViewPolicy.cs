namespace RevitModelMcp.Core.Control;

public sealed record ShowViewCandidate(long Id, long? LevelId, bool IsFloorPlan, bool IsThreeDimensional,
    bool IsTemplate, bool IsPerspective, bool IsActive, bool IsOpen,
    bool ContainsElementBounds = true, bool IsCropActive = false, bool IsDefaultThreeDimensional = false);

public static class ShowViewPolicy
{
    public static ShowViewCandidate? Choose(IEnumerable<ShowViewCandidate> candidates, IReadOnlyList<long> elementLevels) =>
        Rank(candidates, elementLevels).FirstOrDefault();

    public static IEnumerable<ShowViewCandidate> Rank(IEnumerable<ShowViewCandidate> candidates, IReadOnlyList<long> elementLevels)
    {
        var commonLevel = elementLevels.Count > 0 && elementLevels.All(level => level >= 0 && level == elementLevels[0])
            ? (long?)elementLevels[0] : null;
        return candidates
            .Where(view => !view.IsTemplate && !view.IsPerspective && view.ContainsElementBounds &&
                (view.IsThreeDimensional || view.IsFloorPlan && commonLevel.HasValue && view.LevelId == commonLevel))
            .OrderByDescending(view => !view.IsThreeDimensional)
            .ThenByDescending(view => view.IsThreeDimensional && view.IsDefaultThreeDimensional)
            .ThenBy(view => view.IsCropActive)
            .ThenByDescending(view => view.IsActive)
            .ThenByDescending(view => view.IsOpen)
            .ThenBy(view => view.Id);
    }
}
