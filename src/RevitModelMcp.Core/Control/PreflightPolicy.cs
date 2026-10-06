namespace RevitModelMcp.Core.Control;

public static class GroupFailurePolicy
{
    public static readonly Guid FailureId = new("5e68266d-05c6-4f54-aa65-5f0bac99b04e");

    public static List<long> BlockedIds(IEnumerable<long> matchedIds, IEnumerable<IReadOnlyCollection<long>> groupFailureElementIds)
    {
        var failingIds = groupFailureElementIds.SelectMany(ids => ids).ToHashSet();
        return matchedIds.Where(failingIds.Contains).Distinct().OrderBy(id => id).ToList();
    }

    public static List<long> BlockedIds(IEnumerable<long> requestedIds, IEnumerable<IReadOnlyCollection<long>> groupFailureElementIds,
        Func<long, long?> groupOf)
    {
        var failingIds = groupFailureElementIds.SelectMany(ids => ids).ToHashSet();
        return requestedIds.Where(id => failingIds.Contains(id) || groupOf(id) is long groupId && failingIds.Contains(groupId))
            .Distinct().OrderBy(id => id).ToList();
    }
}

public static class GroupSkipPolicy
{
    public enum GroupSkipDecision { Proceed, SkipSome, RefuseAll }

    public static bool Supports(string command) => command is "move" or "rotate" or "copy" or "mirror" or "change-type";

    public static GroupSkipDecision Decide(int requested, int inGroup) => inGroup == 0
        ? GroupSkipDecision.Proceed
        : requested > 0 && inGroup >= requested ? GroupSkipDecision.RefuseAll : GroupSkipDecision.SkipSome;

    public static string RefusalMessage(int requested) => requested == 1
        ? "The element belongs to a group and Revit allows changes to group members only in group edit mode. Nothing was changed."
        : $"All {requested} elements belong to groups and Revit allows changes to group members only in group edit mode. Nothing was changed.";

    public static string SkipWarning(int count) =>
        $"{count} {(count == 1 ? "element was" : "elements were")} skipped because they belong to groups; Revit allows changes to group members only in group edit mode.";
}

public readonly record struct JoinEnd(int Wall, int End);
public sealed record JoinCandidate(JoinEnd End, CadPlanPoint Point);
public sealed record PreflightBox(double MinX, double MinY, double MaxX, double MaxY);
public sealed record PreflightElement(long Id, int? NewWall, CadPlanPoint? A, CadPlanPoint? B, PreflightBox? Box);
public sealed record PreflightFailure(string Description, IReadOnlyList<PreflightElement> Elements);
public sealed record JoinSkip(JoinEnd End, string Reason);

public static class WallJoinPreflight
{
    public const double ToleranceMm = 10;
    public const int MaxRounds = 6;

    public static List<JoinCandidate> FindCandidates(IReadOnlyList<CadWallPlan> walls, double toleranceMm = ToleranceMm)
    {
        var candidates = new List<JoinCandidate>();
        for (var index = 0; index < walls.Count; index++)
            for (var end = 0; end < 2; end++)
            {
                var point = end == 0 ? walls[index].Start : walls[index].End;
                if (walls.Where((_, other) => other != index).Any(wall => Distance(point, wall.Start, wall.End) <= toleranceMm))
                    candidates.Add(new JoinCandidate(new JoinEnd(index, end), point));
            }
        return candidates;
    }

    public static List<JoinSkip> DeriveSkips(IReadOnlyList<JoinCandidate> candidates,
#if NETFRAMEWORK
        ISet<JoinEnd> alreadySkipped,
#else
        IReadOnlySet<JoinEnd> alreadySkipped,
#endif
        IReadOnlyList<PreflightFailure> failures)
    {
        var skips = new Dictionary<JoinEnd, JoinSkip>();
        foreach (var failure in failures)
        {
            var others = failure.Elements.Where(element => element.A.HasValue && element.B.HasValue).ToList();
            var hasNew = failure.Elements.Any(element => element.NewWall.HasValue);
            foreach (var candidate in candidates)
            {
                if (alreadySkipped.Contains(candidate.End) || skips.ContainsKey(candidate.End)) continue;
                if (hasNew && !failure.Elements.Any(element => element.NewWall == candidate.End.Wall)) continue;
                if (others.Any(element => element.NewWall != candidate.End.Wall &&
                    Distance(candidate.Point, element.A!.Value, element.B!.Value) <= 2 * ToleranceMm))
                    skips.Add(candidate.End, new JoinSkip(candidate.End, failure.Description));
            }
        }
        if (skips.Count == 0)
            foreach (var failure in failures)
            {
                var named = failure.Elements.Where(element => element.NewWall.HasValue).Select(element => element.NewWall!.Value).ToHashSet();
                var boxes = failure.Elements.Where(element => element.NewWall is null && (!element.A.HasValue || !element.B.HasValue) && element.Box is not null)
                    .Select(element => element.Box!).ToList();
                var existingSegments = failure.Elements.Where(element => element.NewWall is null && element.A.HasValue && element.B.HasValue).ToList();
                foreach (var candidate in candidates)
                {
                    if (alreadySkipped.Contains(candidate.End) || skips.ContainsKey(candidate.End)) continue;
                    if (boxes.Any(box => candidate.Point.X >= box.MinX - 1000 && candidate.Point.X <= box.MaxX + 1000 &&
                        candidate.Point.Y >= box.MinY - 1000 && candidate.Point.Y <= box.MaxY + 1000) ||
                        named.Contains(candidate.End.Wall) || named.Count == 0 && existingSegments.Any(element =>
                            Distance(candidate.Point, element.A!.Value, element.B!.Value) <= 2 * ToleranceMm))
                        skips.Add(candidate.End, new JoinSkip(candidate.End, failure.Description));
                }
            }
        return skips.Values.OrderBy(skip => skip.End.Wall).ThenBy(skip => skip.End.End).ToList();
    }

    public static string BuildWarning(int unjoinedEnds, IReadOnlyList<long> nearbyExistingIds, bool converged)
    {
        if (!converged) return $"Join preflight did not converge in {MaxRounds} rounds; all walls were created without joins.";
        var ends = unjoinedEnds == 1 ? "end was" : "ends were";
        var nearby = nearbyExistingIds.Count == 0 ? "" : "; existing walls near them: ids " +
            string.Join(", ", nearbyExistingIds.Take(20)) + (nearbyExistingIds.Count > 20 ? ", ..." : "");
        return $"{unjoinedEnds} wall {ends} left unjoined because Revit could not keep or cut the joins there{nearby}.";
    }

    private static double Distance(CadPlanPoint point, CadPlanPoint start, CadPlanPoint end)
    {
        var direction = end - start;
        var lengthSquared = direction.Dot(direction);
        var fraction = lengthSquared == 0 ? 0 : Math.Max(0, Math.Min(1, (point - start).Dot(direction) / lengthSquared));
        var offset = point - (start + direction * fraction);
        return Math.Sqrt(offset.Dot(offset));
    }
}
