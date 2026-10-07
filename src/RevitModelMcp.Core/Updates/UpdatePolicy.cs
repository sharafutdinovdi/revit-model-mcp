using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RevitModelMcp.Core.Updates;

public static class UpdatePolicy
{
    public static string BuildReleaseUrl(string releasePage, string? releaseTag, string version)
    {
        var tag = string.IsNullOrWhiteSpace(releaseTag) ? $"v{version.TrimStart('v', 'V')}" : releaseTag;
        return $"{releasePage}/tag/{tag}";
    }

    public static bool IsNewerStable(string installed, string candidate, bool draft, bool prerelease)
    {
        if (draft || prerelease || !Regex.IsMatch(candidate, @"\Av?\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant)) return false;
        var installedVersion = installed.TrimStart('v', 'V').Split('+')[0];
        var installedPrerelease = installedVersion.Contains('-');
        var current = installedVersion.Split('-')[0];
        var available = candidate.TrimStart('v', 'V').Split('+')[0];
        return Version.TryParse(current, out var currentVersion) &&
               Version.TryParse(available, out var availableVersion) &&
               (availableVersion > currentVersion || installedPrerelease && availableVersion == currentVersion);
    }

    public static bool ShouldCheck(DateTimeOffset now, DateTimeOffset? lastCheck) =>
        lastCheck is null || now - lastCheck.Value >= TimeSpan.FromHours(24);

    public static bool IsEnabled(bool? msiSetting, bool? machineSetting, bool? userSetting,
        bool managedInstall = false, string? environmentOptOut = null) =>
        !managedInstall && environmentOptOut != "1" &&
        msiSetting != false && machineSetting != false && userSetting != false;

    public static string? SelectSingleUserAsset(IEnumerable<string> names, string version)
    {
        var expected = $"RevitModelMcp-{version.TrimStart('v', 'V')}-SingleUser.msi";
        return names.SingleOrDefault(name => string.Equals(name, expected, StringComparison.Ordinal));
    }

    public static string? ParseChecksum(string text, string fileName)
    {
        foreach (var line in text.Split('\n'))
        {
            var value = line.TrimStart('\uFEFF').TrimEnd('\r');
            if (value.Length != 66 + fileName.Length || value[64] != ' ' || value[65] != ' ') continue;
            if (!string.Equals(value.Substring(66), fileName, StringComparison.Ordinal)) continue;
            var checksum = value.Substring(0, 64);
            if (checksum.All(Uri.IsHexDigit)) return checksum;
        }
        return null;
    }

    public static bool VerifyChecksum(Stream file, string expected)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false;
        using var sha256 = SHA256.Create();
        var actual = BitConverter.ToString(sha256.ComputeHash(file)).Replace("-", string.Empty);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
