namespace RevitModelMcp.Core.Batch;

public sealed record SnapshotSourceResolution(string? Path, string Kind, int? SavedInYear,
    bool? UpgradedInMemory, string? SkipReason)
{
    public static SnapshotSourceResolution Resolve(string? documentPath, bool isDetached,
        string? batchPath = null, int? batchSavedInYear = null, bool? batchUpgradedInMemory = null)
    {
        if (!string.IsNullOrWhiteSpace(batchPath))
            return new(batchPath, KindOf(batchPath), batchSavedInYear, batchUpgradedInMemory, null);
        if (isDetached)
            return new(null, "local", null, null, "detached document has no source file");
        var path = string.IsNullOrWhiteSpace(documentPath) ? null : documentPath;
        return new(path, KindOf(path), null, null, null);
    }

    private static string KindOf(string? path) => path?.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase) == true
        ? "rsn" : path?.StartsWith(@"\\", StringComparison.Ordinal) == true ? "unc" : "local";
}
