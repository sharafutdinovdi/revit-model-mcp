using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Output;

internal static class ChannelDirectory
{
    private sealed class ChannelAclRefusedException(string reason) : Exception(reason);

    private static readonly string DefaultRootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitModelMcp");
    private static readonly string? OverrideRootDirectory = Environment.GetEnvironmentVariable("REVIT_MCP_CHANNEL_DIR") is { Length: > 0 } directory
        ? directory
        : null;
    private static string _rootDirectory = OverrideRootDirectory ?? DefaultRootDirectory;
    private static string _outputDirectory = Path.Combine(
        _rootDirectory, "instances", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));

    internal static bool AclRepairAvailable { get; private set; }

    internal static string RootDirectory => _rootDirectory;
    internal static string OutputDirectory => _outputDirectory;

    internal static string StartedUtc { get; } = DateTime.UtcNow.ToString("O");

    internal static (bool Enabled, string? Warning) InitializeChannel()
    {
        AclRepairAvailable = false;
        SecurityIdentifier currentUser;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            currentUser = identity.User ?? throw new InvalidOperationException("The current user SID is unavailable.");
        }
        catch (Exception exception)
        {
            return (false, $"File channel disabled: Current Windows identity is unavailable ({exception.GetType().Name}).");
        }
        try
        {
            EnsureDirectory(RootDirectory, currentUser, OverrideRootDirectory is null);
            EnsureDirectory(Path.Combine(RootDirectory, "instances"), currentUser, OverrideRootDirectory is null);
            ReplaceStaleOwnDirectory(OutputDirectory, currentUser);
            EnsureDirectory(OutputDirectory, currentUser, OverrideRootDirectory is null);
            PruneStaleInstanceDirectories(Path.Combine(RootDirectory, "instances"));
            return (true, null);
        }
        catch (Exception exception)
        {
            var reason = exception is ChannelAclRefusedException
                ? exception.Message
                : $"Directory ACL could not be read or set ({exception.GetType().Name}).";
            if (OverrideRootDirectory is not null)
            {
                _rootDirectory = DefaultRootDirectory;
                _outputDirectory = Path.Combine(DefaultRootDirectory, "instances",
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                try
                {
                    EnsureDirectory(RootDirectory, currentUser, true);
                    EnsureDirectory(Path.Combine(RootDirectory, "instances"), currentUser, true);
                    ReplaceStaleOwnDirectory(OutputDirectory, currentUser);
                    EnsureDirectory(OutputDirectory, currentUser, true);
                }
                catch (Exception fallbackException)
                {
                    AclRepairAvailable = IsAclFailure(exception) || IsAclFailure(fallbackException);
                    return (false, $"File channel disabled: {reason} Private default directory failed ({fallbackException.GetType().Name}).");
                }
                PruneStaleInstanceDirectories(Path.Combine(RootDirectory, "instances"));
                return (true, $"File channel override refused: {reason} Using the private default directory.");
            }
            AclRepairAvailable = IsAclFailure(exception);
            return (false, $"File channel disabled: {reason} Check the channel directory ownership and write permissions.");
        }
    }

    private static bool IsAclFailure(Exception exception) =>
        exception is ChannelAclRefusedException or UnauthorizedAccessException or System.Security.SecurityException;

    private static void EnsureDirectory(string path, SecurityIdentifier currentUser, bool migrateOwnedDirectory)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            var security = PrivateDirectorySecurity(currentUser);
#if NETFRAMEWORK
            Directory.CreateDirectory(path, security);
#else
            security.CreateDirectory(path);
#endif
        }

        var currentSecurity = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = currentSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (ChannelAclPolicy.ShouldProtectExistingDirectory(migrateOwnedDirectory,
                currentSecurity.AreAccessRulesProtected, owner?.Value, currentUser.Value))
        {
            try
            {
                directory.SetAccessControl(PrivateDirectorySecurity(currentUser));
                currentSecurity = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                owner = currentSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            }
            catch (Exception)
            {
                // The existing ACL is evaluated below when migration fails.
            }
        }

        var reason = AclRefusalReason(currentSecurity, currentUser, out _);
        if (reason is not null) throw new ChannelAclRefusedException(reason);
    }

    private static string? AclRefusalReason(DirectorySecurity security, SecurityIdentifier currentUser,
        out SecurityIdentifier? owner)
    {
        owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var entries = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new ChannelAclEntry(
                rule.IdentityReference.Value,
                (int)rule.FileSystemRights,
                rule.AccessControlType == AccessControlType.Allow,
                rule.IsInherited));
        return ChannelAclPolicy.RefusalReason(owner?.Value, currentUser.Value, entries);
    }

    private static void ReplaceStaleOwnDirectory(string path, SecurityIdentifier currentUser)
    {
        if (!Directory.Exists(path)) return;

        var isReparsePoint = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        var reason = "reparse-point";
        using var process = Process.GetCurrentProcess();
        if (!isReparsePoint)
        {
            var directoryCreatedUtc = Directory.GetCreationTimeUtc(path);
            var processStartedUtc = process.StartTime.ToUniversalTime();
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var refusalReason = AclRefusalReason(security, currentUser, out var owner);
            if (ChannelDirectoryPolicy.DecideOwnDirectory(refusalReason, directoryCreatedUtc, processStartedUtc)
                == ChannelDirectoryAction.Keep) return;

            var predatesProcess = ChannelDirectoryPolicy.DecideOwnDirectory(null, directoryCreatedUtc, processStartedUtc)
                == ChannelDirectoryAction.Replace;
            // Current-user ACL migration remains the responsibility of EnsureDirectory.
            if (!predatesProcess && string.Equals(owner?.Value, currentUser.Value, StringComparison.OrdinalIgnoreCase)) return;
            reason = predatesProcess ? "predates-process" : "acl";
        }

        var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The channel directory parent is unavailable.");
        var aside = Path.Combine(parent, ChannelDirectoryPolicy.AsideName(process.Id, Guid.NewGuid().ToString("N").Substring(0, 8)));
        Directory.Move(path, aside);
        PluginLog.Info($"Stale channel directory replaced. Reason='{reason}'.");
        try
        {
            var asideIsReparsePoint = (File.GetAttributes(aside) & FileAttributes.ReparsePoint) != 0;
            Directory.Delete(aside, !asideIsReparsePoint);
        }
        catch (Exception)
        {
            // Failed deletions remain eligible for retention pruning.
        }
    }

    private static void PruneStaleInstanceDirectories(string instancesDirectory)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var nowUtc = DateTime.UtcNow;
            var count = 0;
            foreach (var path in Directory.EnumerateDirectories(instancesDirectory))
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    var name = Path.GetFileName(path);
                    if (!ChannelDirectoryPolicy.TryParseInstanceDirectoryName(name, out var processId, out _)) continue;
                    if (!ChannelDirectoryPolicy.ShouldPrune(name, process.Id, IsProcessAlive(processId),
                            Directory.GetLastWriteTimeUtc(path), nowUtc, ChannelDirectoryPolicy.DefaultRetention)) continue;
                    Directory.Delete(path, true);
                    count++;
                }
                catch (Exception)
                {
                    // A child directory failure does not interrupt pruning.
                }
            }
            if (count > 0) PluginLog.Info($"Pruned stale channel directories. Count={count}.");
        }
        catch (Exception)
        {
            // Retention cleanup does not affect channel initialization.
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal sealed record RepairTarget(string Path, string SecurityDescriptor, ChannelAclRepairPlan Plan);

    internal static IReadOnlyList<RepairTarget> InspectRepair()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new InvalidOperationException("The current user SID is unavailable.");
        var targets = new List<RepairTarget>();
        Inspect(new DirectoryInfo(RootDirectory));
        return targets;

        void Inspect(FileSystemInfo item)
        {
            item.Refresh();
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Channel repair refuses reparse points. Inspect the channel directory manually.");
            var security = item is DirectoryInfo directory
                ? (FileSystemSecurity)directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : ((FileInfo)item).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var entries = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(rule => new ChannelAclEntry(rule.IdentityReference.Value, (int)rule.FileSystemRights,
                    rule.AccessControlType == AccessControlType.Allow, rule.IsInherited));
            targets.Add(new RepairTarget(item.FullName,
                security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access),
                ChannelAclRepairPolicy.Plan(owner?.Value, currentUser.Value, security.AreAccessRulesProtected, entries)));
            if (item is DirectoryInfo parent)
                foreach (var child in parent.EnumerateFileSystemInfos()) Inspect(child);
        }
    }

    internal static bool Repair(IReadOnlyList<RepairTarget> confirmed)
    {
        var current = InspectRepair();
        if (current.Count != confirmed.Count || current.Any(target => !confirmed.Any(previous =>
                previous.Path == target.Path && previous.SecurityDescriptor == target.SecurityDescriptor)))
            throw new InvalidOperationException("Channel permissions changed since confirmation. Review and confirm again.");
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new InvalidOperationException("The current user SID is unavailable.");
        var ownerChangeDenied = false;
        // Children are protected first; parent inheritance changes must not alter the reviewed child ACLs.
        foreach (var target in confirmed.Reverse())
        {
            if (!target.Plan.Required) continue;
            if ((File.GetAttributes(target.Path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Channel repair refuses reparse points.");
            var directory = Directory.Exists(target.Path);
            FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var entry in target.Plan.TargetEntries)
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(entry.Sid),
                    (FileSystemRights)entry.RightsMask,
                    directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                    PropagationFlags.None, AccessControlType.Allow));
            if (directory) new DirectoryInfo(target.Path).SetAccessControl((DirectorySecurity)security);
            else new FileInfo(target.Path).SetAccessControl((FileSecurity)security);
            try
            {
                FileSystemSecurity ownerSecurity = directory ? new DirectorySecurity() : new FileSecurity();
                ownerSecurity.SetOwner(currentUser);
                if (directory) new DirectoryInfo(target.Path).SetAccessControl((DirectorySecurity)ownerSecurity);
                else new FileInfo(target.Path).SetAccessControl((FileSecurity)ownerSecurity);
            }
            catch (UnauthorizedAccessException)
            {
                ownerChangeDenied = true;
                PluginLog.Warn("Channel DACL repaired; Windows refused the owner change. The owner is checked during initialization.");
            }
        }
        return ownerChangeDenied;
    }

    private static DirectorySecurity PrivateDirectorySecurity(SecurityIdentifier currentUser)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(currentUser);
        foreach (var entry in ChannelAclRepairPolicy.Plan(currentUser.Value, currentUser.Value, true, []).TargetEntries)
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(entry.Sid), (FileSystemRights)entry.RightsMask,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

}
