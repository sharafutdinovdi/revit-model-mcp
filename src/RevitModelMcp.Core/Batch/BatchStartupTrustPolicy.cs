namespace RevitModelMcp.Core.Batch;

public enum BatchStartupDecision { Continue, TrustPrompt, StartupDeadline, Cancelled, WorkerExited }

public static class BatchStartupTrustPolicy
{
    public static readonly TimeSpan ObservationBudget = TimeSpan.FromSeconds(90);

    public static bool ShouldInspect(DateTimeOffset launched, DateTimeOffset now) =>
        now - launched <= ObservationBudget;

    public static bool MatchesPrompt(string title, string text) =>
        string.Equals(title, "Security - Unsigned Add-In", StringComparison.OrdinalIgnoreCase) ||
        text.IndexOf("RevitModelMcp.dll", StringComparison.OrdinalIgnoreCase) >= 0;

    public static BatchStartupDecision Decide(bool cancelled, bool workerExited, bool promptObserved,
        DateTimeOffset now, DateTimeOffset deadline)
    {
        if (cancelled) return BatchStartupDecision.Cancelled;
        if (workerExited) return BatchStartupDecision.WorkerExited;
        if (promptObserved) return BatchStartupDecision.TrustPrompt;
        if (now >= deadline) return BatchStartupDecision.StartupDeadline;
        return BatchStartupDecision.Continue;
    }

    public static string Message(int year) =>
        $"Revit {year} asks to trust the unsigned Revit Model MCP add-in. Start Revit {year} once, choose Always Load, close Revit normally, then rerun.";
}
