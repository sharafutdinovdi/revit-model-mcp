using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Policy;
using RevitModelMcp.Core.Updates;
using RevitModelMcp.Updates;

namespace RevitModelMcp.Policy;

/// <summary>
/// Reads the machine policy: HKLM\Software\Policies\RevitModelMcp wins over %ProgramData%\RevitModelMcp\policy.json.
/// A source that cannot be read, parsed or trusted turns the whole policy into the fail-closed state (read-only, code off, updates off, no HTTP):
/// the last good policy is deliberately not kept, because it can be looser than what the administrator wrote since.
/// The merged result is cached for a few seconds so an administrator change applies without a Revit restart.
/// </summary>
internal static class PolicyProvider
{
    private const long CacheMilliseconds = 500;
    private static readonly object SyncRoot = new();
    private static MachinePolicy _current = MachinePolicy.Empty;
    private static long _loadedAt = -CacheMilliseconds - 1;
    private static readonly PolicyLogState LogState = new();

    public static string PolicyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RevitModelMcp", "policy.json");

    public static MachinePolicy Current
    {
        get
        {
            IReadOnlyList<string> messages = [];
            MachinePolicy current;
            lock (SyncRoot)
            {
                if (Now() - _loadedAt > CacheMilliseconds)
                {
                    _current = MachinePolicy.Merge(ReadRegistry(), ReadFile());
                    _loadedAt = Now();
                    messages = LogState.NewMessages(_current);
                }
                current = _current;
            }
            // The logger rotates files and reads the policy, so it must run outside the lock.
            foreach (var message in messages) PluginLog.Warn(message);
            return current;
        }
    }

    /// <summary>The read-only refusal text: the policy text when the policy enforces it, otherwise the plain gate text.</summary>
    public static string ReadOnlyMessage => Control.ActionCommandExecutor.ReadOnlyLock.State.Refusal;

    /// <summary>The refusal for a command under ToolDeny, ToolAllow and ExecuteCode, or null.</summary>
    public static string? Refusal(string? command)
    {
        var policy = Current;
        if (policy.Refusal(command) is { } refusal) return refusal;
        if (!MachinePolicy.IsCodeCommand(command)) return null;
        return CodeExecutionGate.Refusal(command, Channel, policy, UserExecuteCode(policy));
    }

    /// <summary>The policy summary for ping and the handshake, with the effective code state in the store channel.</summary>
    public static Dictionary<string, string> Summary()
    {
        var policy = Current;
        return CodeExecutionGate.Summarize(Channel, policy, UserExecuteCode(policy));
    }

    private static readonly UpdateChannel Channel = UpdateService.Channel();
    private static string? _userExecuteCode;
    private static long _userExecuteCodeAt = -CacheMilliseconds - 1;

    public static string UserSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "settings.json");

    /// <summary>The user's executeCode value, read only in the store channel when no policy value decides, cached like the policy.</summary>
    private static string? UserExecuteCode(MachinePolicy policy)
    {
        if (!CodeExecutionGate.NeedsUserSetting(Channel, policy)) return null;
        lock (SyncRoot)
        {
            if (Now() - _userExecuteCodeAt <= CacheMilliseconds) return _userExecuteCode;
            _userExecuteCodeAt = Now();
            try
            {
                _userExecuteCode = null;
                if (!File.Exists(UserSettingsPath)) return null;
                using var stream = new FileStream(UserSettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _userExecuteCode = (new DataContractJsonSerializer(typeof(CodeSetting)).ReadObject(stream) as CodeSetting)?.ExecuteCode;
            }
            catch (Exception)
            {
                // An unreadable settings file keeps code off.
                _userExecuteCode = null;
            }
            return _userExecuteCode;
        }
    }

    [DataContract]
    private sealed class CodeSetting
    {
        [DataMember(Name = "executeCode")] public string? ExecuteCode { get; set; }
    }

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
            // File.Exists answers false when access is denied, which would read as "no policy". Listing the folder tells the cases apart.
            string[] found;
            try
            {
                found = Directory.GetFiles(folder, Path.GetFileName(path));
            }
            catch (DirectoryNotFoundException)
            {
                return MachinePolicy.Empty;
            }
            if (found.Length == 0) return MachinePolicy.Empty;
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
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var entries = new List<AccessEntry>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow)
                entries.Add(new AccessEntry(rule.IdentityReference.Value, PolicyFileTrust.GrantsWrite((uint)rule.FileSystemRights)));
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
}
