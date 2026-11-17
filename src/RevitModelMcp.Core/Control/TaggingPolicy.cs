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

    public static bool CanTemporarilyIsolate(int independentTargetCount, bool includesRooms,
        bool supportsTemporaryVisibility, bool hasActiveTemporaryMode) =>
        independentTargetCount > 0 && !includesRooms && supportsTemporaryVisibility && !hasActiveTemporaryMode;
}
