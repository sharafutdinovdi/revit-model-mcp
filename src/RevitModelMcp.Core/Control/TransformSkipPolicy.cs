namespace RevitModelMcp.Core.Control;

public enum TransformSkipReason { Pinned, InGroup, Hosted, Constrained }

/// <summary>
/// Decides which elements a move, rotate or in-place mirror cannot change and how the skips are reported.
/// </summary>
public static class TransformSkipPolicy
{
    public const double PoseTolerance = 0.001;

    public static bool Supports(string command, bool copy) => command is "move" or "rotate" || command == "mirror" && !copy;

    public static TransformSkipReason? ClassifyBefore(bool pinned, bool inGroup) =>
        pinned ? TransformSkipReason.Pinned : inGroup ? TransformSkipReason.InGroup : null;

    public static TransformSkipReason ClassifyUnchanged(bool hosted) =>
        hosted ? TransformSkipReason.Hosted : TransformSkipReason.Constrained;

    public static bool SamePose(IReadOnlyList<double>? before, IReadOnlyList<double>? after)
    {
        if (before is null || after is null) return before is null && after is null;
        if (before.Count != after.Count) return false;
        for (var index = 0; index < before.Count; index++)
            if (Math.Abs(before[index] - after[index]) > PoseTolerance) return false;
        return true;
    }

    public static void Add(SkippedByReason skipped, TransformSkipReason reason, long id)
    {
        switch (reason)
        {
            case TransformSkipReason.Pinned: (skipped.Pinned ??= []).Add(id); break;
            case TransformSkipReason.InGroup: skipped.InGroup.Add(id); break;
            case TransformSkipReason.Hosted: (skipped.Hosted ??= []).Add(id); break;
            default: (skipped.Constrained ??= []).Add(id); break;
        }
    }

    public static int Total(SkippedByReason skipped) =>
        skipped.InGroup.Count + (skipped.Pinned?.Count ?? 0) + (skipped.Hosted?.Count ?? 0) + (skipped.Constrained?.Count ?? 0);

    public static string Warning(SkippedByReason skipped)
    {
        var parts = new List<string>();
        if (skipped.InGroup.Count > 0) parts.Add(GroupSkipPolicy.SkipWarning(skipped.InGroup.Count));
        if (skipped.Pinned is { Count: > 0 })
            parts.Add($"{Plural(skipped.Pinned.Count)} skipped because {(skipped.Pinned.Count == 1 ? "it is" : "they are")} pinned; unpin in Revit to change {(skipped.Pinned.Count == 1 ? "it" : "them")}.");
        if (skipped.Hosted is { Count: > 0 })
            parts.Add($"{Plural(skipped.Hosted.Count)} skipped because Revit kept {(skipped.Hosted.Count == 1 ? "it" : "them")} on the host and {(skipped.Hosted.Count == 1 ? "it" : "they")} did not change.");
        if (skipped.Constrained is { Count: > 0 })
            parts.Add($"{Plural(skipped.Constrained.Count)} skipped because a constraint, such as a curtain wall grid, kept {(skipped.Constrained.Count == 1 ? "it" : "them")} in place.");
        return string.Join(" ", parts);
    }

    public static string NoneChangedMessage(string command, int requested, SkippedByReason skipped)
    {
        var verb = command switch { "move" => "moved", "rotate" => "rotated", _ => "mirrored" };
        var reasons = new List<string>();
        if (skipped.Pinned is { Count: > 0 }) reasons.Add($"{skipped.Pinned.Count} pinned");
        if (skipped.InGroup.Count > 0) reasons.Add($"{skipped.InGroup.Count} in groups");
        if (skipped.Hosted is { Count: > 0 }) reasons.Add($"{skipped.Hosted.Count} hosted and unchanged");
        if (skipped.Constrained is { Count: > 0 }) reasons.Add($"{skipped.Constrained.Count} constrained and unchanged");
        return $"None of the {requested} requested elements could be {verb} ({string.Join(", ", reasons)}). Nothing was changed.";
    }

    private static string Plural(int count) => count == 1 ? "1 element was" : $"{count} elements were";
}
