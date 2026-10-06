namespace RevitModelMcp.Core.Control;

public sealed record HttpLimits(int MaxStoredResponses, long MaxStoredBytes, int RequestReadTimeoutSeconds, int ShutdownDrainSeconds)
{
    public const int DefaultMaxStoredResponses = 256;
    public const long DefaultMaxStoredBytes = 64L * 1024 * 1024;
    public const int DefaultRequestReadTimeoutSeconds = 30;
    public const int DefaultShutdownDrainSeconds = 5;

    public static HttpLimits Default { get; } = new(DefaultMaxStoredResponses, DefaultMaxStoredBytes,
        DefaultRequestReadTimeoutSeconds, DefaultShutdownDrainSeconds);

    public string? Validate()
    {
        if (MaxStoredResponses is < 1 or > 10000) return "maxStoredResponses must be between 1 and 10000.";
        if (MaxStoredBytes is < 1048576 or > 1073741824) return "maxStoredBytes must be between 1048576 and 1073741824.";
        if (RequestReadTimeoutSeconds is < 1 or > 300) return "requestReadTimeoutSeconds must be between 1 and 300.";
        if (ShutdownDrainSeconds is < 0 or > 60) return "shutdownDrainSeconds must be between 0 and 60.";
        return null;
    }
}
