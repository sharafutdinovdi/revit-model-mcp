namespace RevitModelMcp.Core.Batch;

public enum BatchDeadlineDecision { Continue, FailAndRecycle, Cancelled }

public static class BatchDeadlinePolicy
{
    public static BatchDeadlineDecision Decide(bool cancelRequested, bool workerExited, DateTimeOffset now,
        DateTimeOffset deadline, DateTimeOffset lastHeartbeat, TimeSpan heartbeatAgeLimit)
    {
        if (cancelRequested) return BatchDeadlineDecision.Cancelled;
        if (workerExited || now >= deadline || now - lastHeartbeat > heartbeatAgeLimit)
            return BatchDeadlineDecision.FailAndRecycle;
        return BatchDeadlineDecision.Continue;
    }
}
