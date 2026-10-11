namespace RevitModelMcp.Core.Policy;

/// <summary>One allow entry of an access control list, reduced to what the trust decision needs.</summary>
/// <param name="Sid">The security identifier of the trustee in SDDL text form.</param>
/// <param name="GrantsWrite">True when the entry allows writing data, deleting, changing permissions or taking ownership.</param>
public readonly record struct AccessEntry(string Sid, bool GrantsWrite);

/// <summary>
/// Decides whether policy.json and its folder can be trusted: only SYSTEM, Administrators and TrustedInstaller may own them or write to them.
/// The ACL itself is read by the add-in; this rule has no Windows dependency so it can be tested anywhere.
/// </summary>
public static class PolicyFileTrust
{
    private static readonly string[] TrustedSids =
    [
        "S-1-5-18",
        "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    ];

    /// <summary>Returns null when the item is trusted, otherwise what is wrong with it.</summary>
    public static string? Check(string what, string? ownerSid, IEnumerable<AccessEntry> entries, bool isReparsePoint)
    {
        if (isReparsePoint) return $"{what} is a link";
        if (ownerSid is null || !IsTrusted(ownerSid)) return $"{what} is not owned by an administrator";
        var writer = entries.FirstOrDefault(entry => entry.GrantsWrite && !IsTrusted(entry.Sid));
        return writer.Sid is null ? null : $"{what} can be changed by a non-administrator ({writer.Sid})";
    }

    // Raw access mask bits (winnt.h): write and append data, delete, delete child, change permissions, take ownership,
    // plus the generic bits, which can appear unmapped in an ACE.
    private const uint WriteEquivalentMask = 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    /// <summary>True when an access mask allows changing the item: any write-equivalent right, including GENERIC_WRITE and GENERIC_ALL.</summary>
    public static bool GrantsWrite(uint accessMask) => (accessMask & WriteEquivalentMask) != 0;

    public static bool IsTrusted(string sid) => TrustedSids.Contains(sid, StringComparer.OrdinalIgnoreCase);
}
