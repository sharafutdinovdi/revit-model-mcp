using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Policy;

namespace RevitModelMcp.Policy;

/// <summary>
/// Reads the machine policy: HKLM\Software\Policies\RevitModelMcp wins over %ProgramData%\RevitModelMcp\policy.json.
/// The merged result is cached for a few seconds so an administrator change applies without a Revit restart.
/// </summary>
internal static class PolicyProvider
{
    private const long CacheMilliseconds = 10 * 1000;
    private static readonly object SyncRoot = new();
    private static MachinePolicy _current = MachinePolicy.Empty;
    private static long _loadedAt = -CacheMilliseconds - 1;

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
            return File.Exists(path) ? MachinePolicy.ParseJson(File.ReadAllText(path)) : MachinePolicy.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MachinePolicy.Empty;
        }
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
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return MachinePolicy.Empty;
        }
    }
}
