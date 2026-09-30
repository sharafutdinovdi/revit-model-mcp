namespace RevitModelMcp.Core.Batch;

public static class BatchReadOnlyPolicy
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ping", "jobs", "batch-prepass", "batch-open", "model-snapshot", "batch-close"
    };

    public static bool Allows(string? command) => command is not null && Allowed.Contains(command);
}
