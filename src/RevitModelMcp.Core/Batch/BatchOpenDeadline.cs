namespace RevitModelMcp.Core.Batch;

public static class BatchOpenDeadline
{
    public const int DefaultMinutes = 30;
    public const int UpgradedInMemoryDefaultMinutes = 45;
    public const int MinimumMinutes = 5;
    public const int MaximumMinutes = 180;

    public static TimeSpan Resolve(int? configuredMinutes, bool upgradedInMemory)
    {
        if (configuredMinutes is int minutes && minutes is < MinimumMinutes or > MaximumMinutes)
            throw new ArgumentOutOfRangeException(nameof(configuredMinutes), configuredMinutes,
                $"Open timeout must be from {MinimumMinutes} through {MaximumMinutes} minutes.");

        return TimeSpan.FromMinutes(configuredMinutes ??
            (upgradedInMemory ? UpgradedInMemoryDefaultMinutes : DefaultMinutes));
    }
}
