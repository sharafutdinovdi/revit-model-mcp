using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Autodesk.Revit.UI;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Formatting;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Output;

internal static class SnapshotService
{
    public static SnapshotRunResult CaptureAndWrite(UIApplication uiApplication, DateTimeOffset localNow)
    {
        var snapshot = SnapshotCollector.Capture(uiApplication, localNow);
        var writeResult = SnapshotFileWriter.Write(snapshot, localNow.LocalDateTime);
        return new SnapshotRunResult(snapshot, writeResult);
    }
}

internal sealed class SnapshotRunResult
{
    public SnapshotRunResult(Snapshot snapshot, SnapshotWriteResult writeResult)
    {
        Snapshot = snapshot;
        WriteResult = writeResult;
    }

    public Snapshot Snapshot { get; }

    public SnapshotWriteResult WriteResult { get; }
}

internal static class SnapshotFileWriter
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

    internal static string RootDirectory => _rootDirectory;
    internal static string OutputDirectory => _outputDirectory;

    internal static string StartedUtc { get; } = DateTime.UtcNow.ToString("O");

    internal static string? InitializeChannel()
    {
        SecurityIdentifier currentUser;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            currentUser = identity.User ?? throw new InvalidOperationException("The current user SID is unavailable.");
        }
        catch (Exception exception)
        {
            return $"File channel disabled: Current Windows identity is unavailable ({exception.GetType().Name}).";
        }
        try
        {
            EnsureDirectory(RootDirectory, currentUser, OverrideRootDirectory is null);
            EnsureDirectory(Path.Combine(RootDirectory, "instances"), currentUser, OverrideRootDirectory is null);
            EnsureDirectory(OutputDirectory, currentUser, OverrideRootDirectory is null);
            return null;
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
                    EnsureDirectory(OutputDirectory, currentUser, true);
                }
                catch (Exception fallbackException)
                {
                    return $"File channel disabled: {reason} Private response directory failed ({fallbackException.GetType().Name}). Check REVIT_MCP_CHANNEL_DIR permissions.";
                }
            }
            return $"File channel disabled: {reason} Check the channel directory ownership and write permissions, including REVIT_MCP_CHANNEL_DIR when set.";
        }
    }

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
        if (migrateOwnedDirectory && owner?.Equals(currentUser) == true)
        {
            directory.SetAccessControl(PrivateDirectorySecurity(currentUser));
            currentSecurity = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            owner = currentSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }

        var entries = currentSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new ChannelAclEntry(
                rule.IdentityReference.Value,
                (int)rule.FileSystemRights,
                rule.AccessControlType == AccessControlType.Allow,
                rule.IsInherited));
        var reason = ChannelAclPolicy.RefusalReason(owner?.Value, currentUser.Value, entries);
        if (reason is not null) throw new ChannelAclRefusedException(reason);
    }

    private static DirectorySecurity PrivateDirectorySecurity(SecurityIdentifier currentUser)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(currentUser);
        foreach (var principal in new[]
                 {
                     currentUser,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    public static SnapshotWriteResult Write(Snapshot snapshot, DateTime localTime)
    {
        var outputDirectory = OutputDirectory;
        var latestPath = Path.Combine(outputDirectory, "latest.json");
        var historyPath = Path.Combine(outputDirectory, $"snapshot_{localTime:yyyyMMdd_HHmmss}.json");
        var summaryPath = Path.Combine(outputDirectory, "latest.txt");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            var encoding = new UTF8Encoding(false);
            var json = SnapshotJsonSerializer.Serialize(snapshot);
            if (ResponseDelivery.Current is { } delivery)
            {
                delivery(json);
                return SnapshotWriteResult.Succeeded(latestPath);
            }
            File.WriteAllText(latestPath, json, encoding);
            File.WriteAllText(historyPath, json, encoding);
            File.WriteAllText(summaryPath, SnapshotSummaryFormatter.Format(snapshot), encoding);
            return SnapshotWriteResult.Succeeded(latestPath);
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Legacy snapshot response write failed. Path='{latestPath}'.", exception);
            return SnapshotWriteResult.Failed(latestPath, exception.Message);
        }
    }
}

internal sealed class SnapshotWriteResult
{
    private SnapshotWriteResult(bool success, string latestPath, string? error)
    {
        Success = success;
        LatestPath = latestPath;
        Error = error;
    }

    public bool Success { get; }

    public string LatestPath { get; }

    public string? Error { get; }

    public static SnapshotWriteResult Succeeded(string latestPath)
    {
        return new SnapshotWriteResult(true, latestPath, null);
    }

    public static SnapshotWriteResult Failed(string latestPath, string error)
    {
        return new SnapshotWriteResult(false, latestPath, error);
    }
}
