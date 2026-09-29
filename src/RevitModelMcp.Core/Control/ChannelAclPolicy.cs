namespace RevitModelMcp.Core.Control;

public sealed record ChannelAclEntry(string Sid, int RightsMask, bool Allow, bool Inherited);

public static class ChannelAclPolicy
{
    private const string LocalSystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string CreatorOwnerSid = "S-1-3-0";
    private const string OwnerRightsSid = "S-1-3-4";
    private const int WritableRights = 0x500D0156;

    public static string? RefusalReason(string? ownerSid, string currentUserSid, IEnumerable<ChannelAclEntry> entries)
    {
        if (currentUserSid is null) throw new ArgumentNullException(nameof(currentUserSid));
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        if (string.IsNullOrWhiteSpace(currentUserSid)) return "The current user SID is unavailable.";
        if (!IsTrusted(ownerSid, currentUserSid)) return "The directory owner is not the current user, SYSTEM or Administrators.";

        foreach (var entry in entries)
        {
            if (entry.Allow && !IsTrustedWriter(entry.Sid, currentUserSid) && (entry.RightsMask & WritableRights) != 0)
                return $"Write access is granted to another principal ({entry.Sid}).";
        }

        return null;
    }

    public static bool ShouldProtectExistingDirectory(bool migrateOwnedDirectory, bool accessRulesProtected,
        string? ownerSid, string currentUserSid)
    {
        if (currentUserSid is null) throw new ArgumentNullException(nameof(currentUserSid));

        return migrateOwnedDirectory && !accessRulesProtected &&
            string.Equals(ownerSid, currentUserSid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrustedWriter(string? sid, string currentUserSid) =>
        IsTrusted(sid, currentUserSid) ||
        string.Equals(sid, CreatorOwnerSid, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sid, OwnerRightsSid, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrusted(string? sid, string currentUserSid) =>
        string.Equals(sid, currentUserSid, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sid, LocalSystemSid, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sid, AdministratorsSid, StringComparison.OrdinalIgnoreCase);
}
