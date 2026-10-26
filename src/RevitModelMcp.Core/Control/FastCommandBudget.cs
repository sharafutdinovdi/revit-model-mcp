namespace RevitModelMcp.Core.Control;

public static class FastCommandBudget
{
    public const long MaximumDurationMs = 60_000;

    public static bool IsPartialAfterBudget(string command, long elapsedMs)
    {
        return elapsedMs >= MaximumDurationMs && !IsImageCommand(command);
    }

    public static bool ShouldStopBetweenFamilies(long elapsedMs, bool cancellationRequested)
    {
        return cancellationRequested || IsPartialAfterBudget("family-audit", elapsedMs);
    }

    public static bool IsImageCommand(string command)
    {
        return command is "export-view" or "capture-elements";
    }
}
