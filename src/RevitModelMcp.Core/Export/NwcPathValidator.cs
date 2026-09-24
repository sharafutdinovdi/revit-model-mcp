namespace RevitModelMcp.Core.Export;

public static class NwcPathValidator
{
    public static void Validate(string path)
    {
        var normalized = EnsureAbsoluteNoTraversal(path);
        if (!normalized.EndsWith(".nwc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path must have the .nwc extension.");
        var drive = normalized.Length >= 2 && normalized[1] == ':';
        var name = normalized.Substring(normalized.LastIndexOf('\\') + 1);
        if (name.Length <= 4) throw new ArgumentException("path must have a non-empty file name.");
        var stem = name.Substring(0, name.Length - 4).Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9")
            throw new ArgumentException("path contains an invalid file name.");
        if (normalized.Any(character => character < 32 || "<>\"|?*".Contains(character)))
            throw new ArgumentException("path contains invalid characters.");
        if (normalized.Substring(drive ? 3 : 2).Contains(':'))
            throw new ArgumentException("path contains invalid characters.");
        if (name.TrimEnd('.', ' ') != name) throw new ArgumentException("path contains an invalid file name.");
    }

    /// <summary>Rejects null/empty, device-prefixed (<c>\\?\</c>, <c>\\.\</c>) and non-absolute paths, and paths with a <c>..</c> segment. Returns the backslash-normalized path.</summary>
    public static string EnsureAbsoluteNoTraversal(string path, string label = "path")
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{label} is required.");
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException("Device paths are not allowed.");
        var normalized = path.Replace('/', '\\');
        var drive = normalized.Length >= 4 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\';
        var unc = normalized.StartsWith("\\\\", StringComparison.Ordinal)
                  && normalized.Split(['\\'], StringSplitOptions.RemoveEmptyEntries).Length >= 3;
        if (!drive && !unc) throw new ArgumentException($"{label} must be an absolute drive or UNC path.");
        if (normalized.Split('\\').Contains("..")) throw new ArgumentException($"{label} must not contain '..' segments.");
        return normalized;
    }
}
