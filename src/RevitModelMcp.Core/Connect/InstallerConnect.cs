using System.ComponentModel;
using System.Diagnostics;

namespace RevitModelMcp.Core.Connect;

public sealed class InstallerConnect
{
    private readonly McpClientConfigurator _configurator;
    private readonly McpServerSpec _spec;
    private readonly Func<string, string, int, string?> _runCommand;

    public InstallerConnect(string appData, string localAppData, string home, McpServerSpec spec,
        string? searchPath, Func<string, string, int, string?>? runCommand = null)
    {
        _spec = spec;
        _configurator = new McpClientConfigurator(spec: spec);
        _runCommand = runCommand ?? RunCommand;
        Targets = McpClientLocator.Discover(appData, localAppData, home)
            .Where(target => Directory.Exists(target.InstallProbe)).ToArray();
        ClaudeExecutable = (searchPath ?? "").Split(Path.PathSeparator)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => Path.Combine(folder.Trim().Trim('"'), "claude.exe"))
            .FirstOrDefault(File.Exists);
    }

    public IReadOnlyList<McpClientTarget> Targets { get; }
    public string? ClaudeExecutable { get; }

    public string Connect(McpClientTarget target)
    {
        if (!Targets.Contains(target)) throw new ArgumentException("The app was not detected.", nameof(target));
        try
        {
            var state = _configurator.Inspect(target).State;
            if (state == McpClientState.Unreadable) return "Failed: the app settings cannot be read. Open Connect AI app in Revit for details.";
            var change = state == McpClientState.ConfiguredDifferentCommand
                ? _configurator.Update(target)
                : _configurator.Add(target);
            return change.Changed ? "Connected. Restart the app." : "Already configured. Restart the app.";
        }
        catch (McpConfigException exception)
        {
            return "Failed: " + exception.Message;
        }
    }

    public string ConnectClaudeCode()
    {
        if (ClaudeExecutable is null) return "Failed: claude.exe was not found on PATH.";
        var error = _runCommand(ClaudeExecutable, _spec.ClaudeCodeArguments, 30000);
        return error is null ? "Connected for this user. Restart Claude Code." : "Failed: " + error;
    }

    public static string? RunCommand(string executable, string arguments, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return "The command could not be started.";
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                process.Kill();
                return "The command timed out. Check Claude Code settings before trying again.";
            }
            return process.ExitCode == 0 ? null : $"Claude Code returned exit code {process.ExitCode}. Run claude mcp list in a terminal for details.";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return exception.Message;
        }
    }
}
