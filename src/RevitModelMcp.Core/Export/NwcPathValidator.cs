namespace RevitModelMcp.Core.Export;

public static class NwcPathValidator
{
    private static string[] _trustedNetworkRoots = [];

    public static void ConfigureTrustedNetworkRoots(IEnumerable<string>? roots)
    {
        var normalizedRoots = (roots ?? []).Select(root =>
        {
            var normalized = root?.Replace('/', '\\').TrimEnd('\\');
            var parts = normalized?.Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
            if (normalized is null || !normalized.StartsWith("\\\\", StringComparison.Ordinal) ||
                normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                normalized.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                parts is not { Length: 2 } || parts.Any(part => part is "." or ".." || part.Contains(':')))
                throw new ArgumentException("trustedNetworkRoots must contain UNC share roots such as \\\\server\\share.");
            return $"\\\\{parts[0]}\\{parts[1]}";
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        System.Threading.Volatile.Write(ref _trustedNetworkRoots, normalizedRoots);
    }

    public static void Validate(string path, IReadOnlyCollection<string>? trustedNetworkRoots = null)
    {
        var normalized = EnsureAbsoluteNoTraversal(path, "path", trustedNetworkRoots);
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

    public static void EnsureSettingsXmlSize(long length)
    {
        if (length > 1024 * 1024)
            throw new ArgumentException("settings_xml exceeds the 1 MiB limit.");
    }

    /// <summary>Rejects empty, device and traversal paths, and UNC paths outside trusted shares. Returns the backslash-normalized path.</summary>
    public static string EnsureAbsoluteNoTraversal(string path, string label = "path", IReadOnlyCollection<string>? trustedNetworkRoots = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{label} is required.");
        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) || normalized.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException("Device paths are not allowed.");
        var drive = normalized.Length >= 4 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\';
        var parts = normalized.Split(['\\'], StringSplitOptions.RemoveEmptyEntries);
        var unc = normalized.StartsWith("\\\\", StringComparison.Ordinal) && parts.Length >= 3;
        if (!drive && !unc) throw new ArgumentException($"{label} must be an absolute drive or UNC path.");
        if (normalized.Split('\\').Contains("..")) throw new ArgumentException($"{label} must not contain '..' segments.");
        if (unc)
        {
            var root = $"\\\\{parts[0]}\\{parts[1]}";
            var allowed = trustedNetworkRoots ?? System.Threading.Volatile.Read(ref _trustedNetworkRoots);
            if (!allowed.Any(item => string.Equals(item.Replace('/', '\\').TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{label} is a network path. Add its share to trustedNetworkRoots in %LOCALAPPDATA%\\RevitModelMcp\\settings.json to allow it.");
        }
        return normalized;
    }
}
