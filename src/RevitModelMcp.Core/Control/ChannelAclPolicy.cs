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

public sealed record ChannelAclRepairPlan(
    string OwnerSid, bool Required, IReadOnlyList<string> RemovedSids, IReadOnlyList<ChannelAclEntry> TargetEntries)
{
    public string Confirmation(string folder, string owner, IEnumerable<string> removedPrincipals,
        IEnumerable<string> changedRelativePaths)
    {
        if (folder is null) throw new ArgumentNullException(nameof(folder));
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        if (removedPrincipals is null) throw new ArgumentNullException(nameof(removedPrincipals));
        if (changedRelativePaths is null) throw new ArgumentNullException(nameof(changedRelativePaths));
        var paths = changedRelativePaths.ToArray();
        var principals = removedPrincipals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var examples = string.Join("\n", paths.Take(5));
        if (paths.Length > 5) examples += $"\nand {paths.Length - 5} more";
        return $"Folder: {folder}\nCurrent owner: {owner}\nPrincipals that will lose access:\n" +
               (principals.Length == 0 ? "None" : string.Join("\n", principals)) +
               $"\nObjects whose permissions will change: {paths.Length}" +
               (paths.Length == 0 ? string.Empty : "\nExample relative paths (. is the channel folder):\n" + examples) +
               "\n\nReplace permissions on this folder and existing children with Full Control for the current user, " +
               "SYSTEM and Administrators only. Turn inheritance off and set the owner to the current user where allowed. " +
               "This changes workstation files, not the model. Continue?";
    }
}

public static class ChannelAclRepairPolicy
{
    public static ChannelAclRepairPlan Plan(string? ownerSid, string currentUserSid, bool accessRulesProtected,
        IEnumerable<ChannelAclEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(currentUserSid)) throw new ArgumentException("The current user SID is unavailable.", nameof(currentUserSid));
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        var existing = entries.ToArray();
        var target = new[] { currentUserSid, "S-1-5-18", "S-1-5-32-544" }
            .Select(sid => new ChannelAclEntry(sid, 0x1F01FF, true, false)).ToArray();
        var trusted = target.Select(entry => entry.Sid).ToArray();
        var removed = existing.Where(entry => !trusted.Contains(entry.Sid, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.Sid).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(sid => sid, StringComparer.Ordinal).ToArray();
        var required = !accessRulesProtected || !string.Equals(ownerSid, currentUserSid, StringComparison.OrdinalIgnoreCase) ||
                       existing.Length != target.Length || target.Any(expected => !existing.Any(entry =>
                           string.Equals(entry.Sid, expected.Sid, StringComparison.OrdinalIgnoreCase) && entry.Allow &&
                           entry.RightsMask == expected.RightsMask && !entry.Inherited));
        return new ChannelAclRepairPlan(ownerSid ?? "Unknown", required, removed, target);
    }

    public static string ManualCommands(string folder, IEnumerable<string>? removedSids = null)
    {
        if (folder is null) throw new ArgumentNullException(nameof(folder));
        var removals = removedSids is null
            ? "# Inspect and remove every remaining foreign SID before restarting Revit.\n"
            : string.Join("\n", removedSids.Distinct(StringComparer.OrdinalIgnoreCase).Select(sid =>
                "icacls $channel /remove '*" + sid.Replace("'", "''") + "' /T\n" +
                "if ($LASTEXITCODE -ne 0) { throw 'SID removal failed' }")) + "\n";
        return "$channel = '" + folder.Replace("'", "''") + "'\n" +
               "$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value\n" +
               "icacls $channel /setowner \"*$sid\" /T\nif ($LASTEXITCODE -ne 0) { throw 'Owner change failed' }\n" +
               "icacls $channel /inheritance:r /grant:r \"*${sid}:(OI)(CI)F\" \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\" /T\nif ($LASTEXITCODE -ne 0) { throw 'DACL change failed' }\n" +
               removals +
               "# Stop on any failure. Inspect the ACL before restarting Revit.";
    }

    public static string ManualInstructions =>
        "Windows refused the permission repair. WRITE_DAC is required to change permissions; WRITE_OWNER may be required to change the owner. " +
        "Some children may already have been repaired. Close Revit and MCP clients. Ask an administrator to inspect the folder, then use an elevated PowerShell under the same Windows account. " +
        "See docs/transport.md#channel-permissions for the icacls commands.";
}
