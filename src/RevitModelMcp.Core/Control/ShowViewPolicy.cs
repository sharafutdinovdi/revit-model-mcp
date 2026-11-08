namespace RevitModelMcp.Core.Control;

public sealed record ShowViewCandidate(long Id, long? LevelId, bool IsFloorPlan, bool IsThreeDimensional,
    bool IsTemplate, bool IsPerspective, bool IsActive, bool IsOpen);

public static class ShowViewPolicy
{
    public static ShowViewCandidate? Choose(IEnumerable<ShowViewCandidate> candidates, IReadOnlyList<long> elementLevels)
    {
        var commonLevel = elementLevels.Count > 0 && elementLevels.All(level => level >= 0 && level == elementLevels[0])
            ? (long?)elementLevels[0] : null;
        return candidates
            .Where(view => !view.IsTemplate && !view.IsPerspective &&
                (view.IsThreeDimensional || commonLevel.HasValue && view.LevelId == commonLevel))
            .OrderByDescending(view => commonLevel.HasValue && view.LevelId == commonLevel)
            .ThenByDescending(view => view.IsActive)
            .ThenByDescending(view => view.IsOpen)
            .ThenByDescending(view => view.IsFloorPlan)
            .ThenBy(view => view.Id)
            .FirstOrDefault();
    }
}
