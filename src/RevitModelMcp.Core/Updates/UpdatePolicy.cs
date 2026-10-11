using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RevitModelMcp.Core.Updates;

/// <summary>An update download or its redirect target is on a host the update policy does not allow.</summary>
public sealed class UpdateDownloadRefusedException(string host) : Exception(UpdatePolicy.RefusalText(host))
{
    public string Host { get; } = host;
}

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
        var installedDev = installedVersion.Contains("-dev", StringComparison.OrdinalIgnoreCase);
        var current = installedVersion.Split('-')[0];
        var available = candidate.TrimStart('v', 'V').Split('+')[0];
        return Version.TryParse(current, out var currentVersion) &&
               Version.TryParse(available, out var availableVersion) &&
               (availableVersion > currentVersion || installedPrerelease && !installedDev && availableVersion == currentVersion);
    }

    public static bool ShouldCheck(DateTimeOffset now, DateTimeOffset? lastCheck) =>
        lastCheck is null || now - lastCheck.Value >= TimeSpan.FromHours(24);

    public static readonly TimeSpan BlockDuration = TimeSpan.FromDays(7);

    private static readonly string[] DefaultDownloadHosts =
        ["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>True when a version was blocked after a failed checksum or install and the block has not expired.</summary>
    public static bool IsBlocked(string version, IReadOnlyDictionary<string, DateTimeOffset> blockedUntil, DateTimeOffset now) =>
        blockedUntil.TryGetValue(version.TrimStart('v', 'V'), out var until) && now < until;

    /// <summary>
    /// Accepts HTTPS URLs without credentials whose host is the feed host. For the default GitHub feed the
    /// GitHub download hosts are accepted too. Loopback HTTP is accepted only when the feed itself is loopback HTTP.
    /// </summary>
    public static bool IsAllowedDownloadUrl(string? value, Uri feed)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0) return false;
        if (uri.Scheme == Uri.UriSchemeHttp)
            return feed.Scheme == Uri.UriSchemeHttp && IsLoopback(uri) && IsLoopback(feed) && uri.Port == feed.Port;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (string.Equals(uri.Host, feed.Host, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(feed.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) &&
               DefaultDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The text for a download or redirect target that is not allowed. It names the host, never the full URL, so no query or credentials leak.</summary>
    public static string RefusalText(string host) => $"Update download refused: host {host} is not allowed.";

    /// <summary>Throws <see cref="UpdateDownloadRefusedException"/> naming the host when <paramref name="value"/> is not an allowed download URL.</summary>
    public static void EnsureAllowedDownloadUrl(string? value, Uri feed)
    {
        if (IsAllowedDownloadUrl(value, feed)) return;
        var host = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length != 0 ? uri.Host : "(unknown)";
        throw new UpdateDownloadRefusedException(host);
    }

    private static bool IsLoopback(Uri uri) => uri.Host is "127.0.0.1" or "localhost";

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
