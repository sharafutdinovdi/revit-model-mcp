using System.Text.RegularExpressions;

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

    public static (string Name, string Path) ParsePrompt(string text)
    {
        var name = Regex.Match(text, @"(?im)^\s*Name:[ \t]*(.+)$").Groups[1].Value.Trim();
        var path = Regex.Match(text, @"(?im)^\s*Location:[ \t]*(.+)$").Groups[1].Value.Trim();
        return (name.Length == 0 ? "unknown add-in" : name,
            path.Length == 0 ? "location unavailable" : path);
    }

    public static string Message(int year, string text = "")
    {
        var prompt = ParsePrompt(text);
        return $"Revit {year} cannot start unattended: unsigned add-in '{prompt.Name}' at '{prompt.Path}' requires trust. " +
               $"Start Revit {year} interactively as the same Windows user with the same installed add-in paths. " +
               "Verify this add-in and choose Always Load only if trusted, then close Revit normally and rerun. " +
               "Load Once does not persist trust. Every installed add-in must allow unattended startup. " +
               "Trust for a development path does not cover the store bundle path in ApplicationPlugins. " +
               "Disable untrusted add-ins before the batch run.";
    }

    public static string UnavailableMessage(int year, string reason) =>
        $"Revit {year} is unavailable for this batch run. {reason}";
}

public sealed class BatchStartupYearAvailability
{
    private readonly Dictionary<int, string> _unavailableYears = [];

    public bool TryGetReason(int year, out string? reason) =>
        _unavailableYears.TryGetValue(year, out reason);

    public void MarkTrustPrompt(int year, string text = "") =>
        _unavailableYears[year] = BatchStartupTrustPolicy.Message(year, text);

    public void MarkStartupDeadline(int year, string failure) =>
        _unavailableYears[year] = BatchStartupTrustPolicy.UnavailableMessage(year, failure);
}
