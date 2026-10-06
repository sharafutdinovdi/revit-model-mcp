using System.Globalization;

namespace RevitModelMcp.Core.Control;

public enum ChannelDirectoryAction
{
    Keep,
    Replace
}

public static class ChannelDirectoryPolicy
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(14);

    public static ChannelDirectoryAction DecideOwnDirectory(string? aclRefusalReason,
        DateTime directoryCreatedUtc, DateTime processStartedUtc)
    {
        return aclRefusalReason is not null || processStartedUtc - directoryCreatedUtc > TimeSpan.FromSeconds(5)
            ? ChannelDirectoryAction.Replace
            : ChannelDirectoryAction.Keep;
    }

    public static bool TryParseInstanceDirectoryName(string name, out int processId, out bool isAside)
    {
        processId = 0;
        isAside = false;
        if (string.IsNullOrEmpty(name)) return false;

        const string asideMarker = ".stale-";
        var markerIndex = name.IndexOf(asideMarker, StringComparison.Ordinal);
        var digits = markerIndex < 0 ? name : name.Substring(0, markerIndex);
        if (digits.Length == 0) return false;
        foreach (var character in digits)
        {
            if (character is < '0' or > '9') return false;
        }

        if (markerIndex >= 0)
        {
            var suffix = name.Substring(markerIndex + asideMarker.Length);
            if (suffix.Length == 0) return false;
            foreach (var character in suffix)
            {
                if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') and not (>= 'A' and <= 'F'))
                    return false;
            }
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out processId)) return false;
        isAside = markerIndex >= 0;
        return true;
    }

    public static bool ShouldPrune(string name, int ownProcessId, bool isProcessAlive,
        DateTime lastWriteUtc, DateTime nowUtc, TimeSpan retention)
    {
        if (!TryParseInstanceDirectoryName(name, out var processId, out var isAside)) return false;
        if (!isAside && (processId == ownProcessId || isProcessAlive)) return false;
        return nowUtc - lastWriteUtc > retention;
    }

    public static string AsideName(int processId, string suffixHex) => $"{processId}.stale-{suffixHex}";
}
