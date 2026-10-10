using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Policy;

namespace RevitModelMcp.Policy;

/// <summary>
/// Reads the machine policy: HKLM\Software\Policies\RevitModelMcp wins over %ProgramData%\RevitModelMcp\policy.json.
/// A source that cannot be read, parsed or trusted turns the whole policy into the fail-closed state (read-only, code off, updates off, no HTTP):
/// the last good policy is deliberately not kept, because it can be looser than what the administrator wrote since.
/// The merged result is cached for a few seconds so an administrator change applies without a Revit restart.
/// </summary>
internal static class PolicyProvider
{
    private const long CacheMilliseconds = 10 * 1000;
    private static readonly object SyncRoot = new();
    private static MachinePolicy _current = MachinePolicy.Empty;
    private static long _loadedAt = -CacheMilliseconds - 1;
    private static string? _loggedProblem;

    public static string PolicyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RevitModelMcp", "policy.json");

    public static MachinePolicy Current
    {
        get
        {
            lock (SyncRoot)
            {
                if (Now() - _loadedAt > CacheMilliseconds)
                {
                    _current = MachinePolicy.Merge(ReadRegistry(), ReadFile());
                    _loadedAt = Now();
                    LogOnce(_current);
                }
                return _current;
            }
        }
    }

    /// <summary>The read-only refusal text: the policy text when the policy enforces it, otherwise the plain gate text.</summary>
    public static string ReadOnlyMessage => Current.ReadOnlyRefusal() ?? ReadOnlyGatePolicy.Message;

    /// <summary>The refusal for a command under ToolDeny, ToolAllow and ExecuteCode, or null.</summary>
    public static string? Refusal(string? command) => Current.Refusal(command);

    /// <summary>Policy values that apply in this process. RedactPaths is forwarded through the environment because the shared Core code reads it there.</summary>
    public static void ApplyProcessEnvironment()
    {
        var redact = Current.RedactPaths;
        if (redact is { } value) Environment.SetEnvironmentVariable("REVIT_MCP_REDACT_PATHS", value ? "1" : "0");
    }

    private static long Now() => Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);

    private static MachinePolicy ReadFile()
    {
        try
        {
            var path = PolicyFilePath;
            var folder = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(folder) || !File.Exists(path)) return MachinePolicy.Empty;
            if (UntrustedLocation(folder, path) is { } untrusted) return MachinePolicy.FailClosed(untrusted);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return MachinePolicy.ParseJson(reader.ReadToEnd());
        }
        catch (Exception exception)
        {
            // Any read failure, including a lock held by another process, must not loosen the policy.
            return MachinePolicy.FailClosed($"the policy file could not be opened ({exception.GetType().Name})");
        }
    }

    /// <summary>The policy file and its folder must be owned by and writable only by administrators; anything else could have been planted by a standard user.</summary>
    private static string? UntrustedLocation(string folder, string path)
    {
        var directory = new DirectoryInfo(folder);
        if (Check("the policy folder", directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
                directory.Attributes) is { } folderProblem) return folderProblem;
        var file = new FileInfo(path);
        return Check("the policy file", file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), file.Attributes);
    }

    private static string? Check(string what, FileSystemSecurity security, FileAttributes attributes)
    {
        const FileSystemRights writeRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var entries = new List<AccessEntry>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow)
                entries.Add(new AccessEntry(rule.IdentityReference.Value, (rule.FileSystemRights & writeRights) != 0));
        return PolicyFileTrust.Check(what, owner, entries, (attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static MachinePolicy ReadRegistry()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(MachinePolicy.RegistryKey);
            if (key is null) return MachinePolicy.Empty;
            var values = new Dictionary<string, object>();
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is { } value) values[name] = value;
            return MachinePolicy.Parse(values);
        }
        catch (Exception exception)
        {
            return MachinePolicy.FailClosed($"the registry policy could not be read ({exception.GetType().Name})");
        }
    }

    /// <summary>Logs a changed unreadable state once per distinct problem, after the cache is updated so the logger can read the policy without recursing.</summary>
    private static void LogOnce(MachinePolicy policy)
    {
        if (!policy.Unreadable)
        {
            _loggedProblem = null;
            return;
        }
        if (_loggedProblem == policy.Problem) return;
        _loggedProblem = policy.Problem;
        PluginLog.Warn($"Organization policy could not be read, so the add-in is read-only, code execution is off and updates are off: {policy.Problem}.");
    }
}
