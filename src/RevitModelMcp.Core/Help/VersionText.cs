namespace RevitModelMcp.Core.Help;

public static class VersionText
{
    public static string Format(string informationalVersion)
    {
        if (informationalVersion is null) throw new ArgumentNullException(nameof(informationalVersion));

        var suffixIndex = informationalVersion.IndexOf('+');
        if (suffixIndex < 0) return informationalVersion;

        var version = informationalVersion.Substring(0, suffixIndex);
        var suffix = informationalVersion.Substring(suffixIndex + 1);
        if (suffix.Length == 0) return version;

        return $"{version} ({suffix.Substring(0, Math.Min(7, suffix.Length))})";
    }
}
