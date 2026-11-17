namespace RevitModelMcp.Core.Control;

public static class TaggingPolicy
{
    // Revit 2026: 200 tags took 57.3 s in 20-target batches, versus 206 s in one batch.
    public const int IsolationBatchSize = 20;

    public static IEnumerable<ArraySegment<T>> PlanIsolationBatches<T>(T[] orderedTargets)
    {
        if (orderedTargets is null) throw new ArgumentNullException(nameof(orderedTargets));
        for (var offset = 0; offset < orderedTargets.Length; offset += IsolationBatchSize)
            yield return new ArraySegment<T>(orderedTargets, offset, Math.Min(IsolationBatchSize, orderedTargets.Length - offset));
    }

    public static string? GetPlainPathReason(int independentTargetCount, bool includesRooms,
        bool supportsTemporaryVisibility, string? activeTemporaryModes)
    {
        if (includesRooms) return "rooms-in-call";
        if (independentTargetCount <= 0) return "no-independent-targets";
        if (!supportsTemporaryVisibility) return "view-cannot-enter-mode";
        return string.IsNullOrEmpty(activeTemporaryModes) ? null : $"temporary-mode:{activeTemporaryModes}";
    }

    public static string BuildPathLog(int isolatedBatches, string? plainPathReason, string? exceptionType)
    {
        var path = plainPathReason is null && isolatedBatches > 0 ? "isolated-batches" : "plain";
        var reason = plainPathReason ?? (isolatedBatches > 0 ? "none" : "no-batches-started");
        return $"Tagging path={path}; batches={isolatedBatches}; batchSize={IsolationBatchSize}; reason={reason}; exception={exceptionType ?? "none"}.";
    }
}
