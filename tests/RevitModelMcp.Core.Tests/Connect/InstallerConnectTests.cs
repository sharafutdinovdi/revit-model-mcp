using RevitModelMcp.Core.Connect;

namespace RevitModelMcp.Core.Tests.Connect;

public sealed class InstallerConnectTests
{
    private static readonly McpServerSpec Server = McpServerSpec.ForExecutable(@"C:\Program Files\Model MCP\server\revit-model-mcp.exe");

    [Test]
    public async Task DiscoveryOffersOnlyExistingProfilesAndNeverWritesSettings()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("Claude"));
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var connection = new InstallerConnect(temp.Path, temp.Path, temp.Path, Server, null);
        var detected = McpClientLocator.Discover(temp.Path, temp.Path, temp.Path).Where(target => Directory.Exists(target.InstallProbe));
        await Assert.That(connection.Targets).IsEquivalentTo(detected);
        await Assert.That(connection.Targets.Count).IsEqualTo(2);
        await Assert.That(connection.ClaudeExecutable).IsNull();
        await Assert.That(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task ConnectChangesOnlyTheChosenAppAndUsesTheSharedConfigurator()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("Claude"));
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var original = "{\"mcpServers\":{\"other\":{\"command\":\"keep\"}},\"theme\":\"dark\"}";
        var path = temp.Combine("Claude", "claude_desktop_config.json");
        File.WriteAllText(path, original);
        var connection = new InstallerConnect(temp.Path, temp.Path, temp.Path, Server, null);
        var desktop = connection.Targets.Single(target => target.Id == "claude-desktop");
        await Assert.That(connection.Connect(desktop)).StartsWith("Connected.");
        await Assert.That(new McpClientConfigurator(spec: Server).Inspect(desktop).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(File.ReadAllText(path)).Contains("\"other\":{\"command\":\"keep\"}");
        await Assert.That(File.ReadAllText(path)).Contains("\"theme\":\"dark\"");
        var backup = Directory.GetFiles(temp.Combine("Claude"), "*.bak").Single();
        await Assert.That(File.ReadAllText(backup)).IsEqualTo(original);
        await Assert.That(File.Exists(temp.Combine(".cursor", "mcp.json"))).IsFalse();
        await Assert.That(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories)).IsEmpty();
        await Assert.That(connection.Connect(desktop)).StartsWith("Already configured.");
        await Assert.That(Directory.GetFiles(temp.Combine("Claude"), "*.bak").Length).IsEqualTo(1);
    }

    [Test]
    public async Task SelectingAnExistingRegistrationUpdatesOnlyOurEntryAfterBackup()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".codex"));
        var connection = new InstallerConnect(temp.Path, temp.Path, temp.Path, Server, null);
        var codex = connection.Targets.Single();
        new McpClientConfigurator().Add(codex);
        var original = File.ReadAllText(codex.ConfigPath);
        await Assert.That(connection.Connect(codex)).StartsWith("Connected.");
        await Assert.That(new McpClientConfigurator(spec: Server).Inspect(codex).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(File.ReadAllText(Directory.GetFiles(temp.Combine(".codex"), "*.bak").Single())).IsEqualTo(original);
    }

    [Test]
    public async Task InvalidSettingsRemainUntouchedWithPlainFailure()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var path = temp.Combine(".cursor", "mcp.json");
        File.WriteAllText(path, "invalid json");
        var connection = new InstallerConnect(temp.Path, temp.Path, temp.Path, Server, null);
        await Assert.That(connection.Connect(connection.Targets.Single())).StartsWith("Failed:");
        await Assert.That(File.ReadAllText(path)).IsEqualTo("invalid json");
        await Assert.That(Directory.GetFiles(temp.Path, "*.bak", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task ClaudeCodeRunsOnlyAfterExplicitSelectionWithSharedUserScopeArguments()
    {
        using var temp = new TempDirectory();
        var executable = temp.Combine("claude.exe");
        File.WriteAllText(executable, "");
        var calls = 0;
        var connection = new InstallerConnect(temp.Path, temp.Path, temp.Path, Server, temp.Path, (command, arguments, timeout) =>
        {
            if (command != executable || "claude " + arguments != Server.ClaudeCodeCommand || timeout != 30000)
                throw new InvalidOperationException("Unexpected Claude Code invocation.");
            calls++;
            return null;
        });
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(connection.ClaudeExecutable).IsEqualTo(executable);
        await Assert.That(connection.ConnectClaudeCode()).StartsWith("Connected for this user.");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ClaudeCodeMissingAndFailedCommandsReportFailure()
    {
        using var temp = new TempDirectory();
        var missing = new InstallerConnect(temp.Path, temp.Path, temp.Path, Server, temp.Path);
        await Assert.That(missing.ConnectClaudeCode()).StartsWith("Failed:");
        File.WriteAllText(temp.Combine("claude.exe"), "");
        var failed = new InstallerConnect(temp.Path, temp.Path, temp.Path, Server, temp.Path, (_, _, _) => "Command timed out.");
        await Assert.That(failed.ConnectClaudeCode()).IsEqualTo("Failed: Command timed out.");
    }

    [Test]
    public async Task ProcessRunnerReportsSuccessExitFailureMissingExecutableAndTimeout()
    {
        var windows = OperatingSystem.IsWindows();
        var executable = windows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh";
        await Assert.That(InstallerConnect.RunCommand(executable, windows ? "/c exit 0" : "-c \"exit 0\"", 10000)).IsNull();
        await Assert.That(InstallerConnect.RunCommand(executable, windows ? "/c exit 7" : "-c \"exit 7\"", 10000)).Contains("exit code 7");
        await Assert.That(InstallerConnect.RunCommand("missing-" + Guid.NewGuid().ToString("N"), "", 10000)).IsNotNull();
        var sleeper = windows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe") : "/bin/sleep";
        await Assert.That(InstallerConnect.RunCommand(sleeper, windows ? "-NoProfile -Command Start-Sleep -Seconds 20" : "20", 100)).Contains("timed out");
    }
}
