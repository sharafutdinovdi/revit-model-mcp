using RevitModelMcp.Core.Connect;

namespace RevitModelMcp.Core.Tests.Connect;

public sealed class ConnectionCheckTests
{
    private const string Initialize = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"serverInfo\":{\"version\":\"2.0.0\"}}}";
    private const string Tools = "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"revit_ping\"}]}}";

    [Test]
    public async Task FileInspectionNeverWritesAndSkipsUndetectedApps()
    {
        using var temporary = new TempDirectory();
        var target = ConnectTestSupport.Desktop with { InstallProbe = temporary.Path, ConfigPath = temporary.Combine("config.json") };
        var text = JsonConfigEditor.Add(target, string.Empty)!;
        File.WriteAllText(target.ConfigPath, text);
        var checks = ConnectionCheck.Clients([target, target with { InstallProbe = temporary.Combine("absent") }], McpServerSpec.Uvx);
        await Assert.That(checks.Count).IsEqualTo(1);
        await Assert.That(checks[0].Level).IsEqualTo(ConnectionCheckLevel.Ok);
        await Assert.That(File.ReadAllText(target.ConfigPath)).IsEqualTo(text);
        await Assert.That(Directory.GetFiles(temporary.Path).Length).IsEqualTo(1);
        File.Delete(target.ConfigPath);
        await Assert.That(ConnectionCheck.Clients([target], McpServerSpec.Uvx)[0].Level).IsEqualTo(ConnectionCheckLevel.Warning);
        await Assert.That(File.Exists(target.ConfigPath)).IsFalse();
    }

    [Test]
    public async Task CancelledProbeDoesNotLaunchTheExecutable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await ConnectionServerProbe.CheckAsync(McpServerSpec.ForExecutable(Environment.ProcessPath!), "2.0.0", cancellation.Token);
        await Assert.That(result.Level).IsEqualTo(ConnectionCheckLevel.Problem);
        await Assert.That(result.Advice).Contains("cancelled");
    }

    [Test]
    public async Task LocalChannelUsesLiveState()
    {
        await Assert.That(ConnectionCheck.LocalChannel(true).Level).IsEqualTo(ConnectionCheckLevel.Ok);
        await Assert.That(ConnectionCheck.LocalChannel(false).Level).IsEqualTo(ConnectionCheckLevel.Problem);
    }

    [Test]
    public async Task ServerDistinguishesMissingExecutableAndOfflineUvx()
    {
        await Assert.That(ConnectionCheck.Server(null, false).Level).IsEqualTo(ConnectionCheckLevel.Problem);
        await Assert.That(ConnectionCheck.Server(McpServerSpec.ForExecutable("missing.exe"), false).Level).IsEqualTo(ConnectionCheckLevel.Problem);
        await Assert.That(ConnectionCheck.Server(McpServerSpec.ForExecutable("server.exe"), true).Level).IsEqualTo(ConnectionCheckLevel.Ok);
        await Assert.That(ConnectionCheck.Server(McpServerSpec.Uvx, true).Level).IsEqualTo(ConnectionCheckLevel.Warning);
        await Assert.That(ConnectionCheck.Server(McpServerSpec.Uvx, true).Advice).Contains("cannot be checked offline");
    }

    [Test]
    public async Task ExactWrittenConfigurationsPassWithoutChanges()
    {
        foreach (var target in new[] { ConnectTestSupport.Desktop, ConnectTestSupport.VsCode, ConnectTestSupport.Codex })
        {
            var spec = McpServerSpec.ForExecutable(@"C:\Model MCP\server.exe");
            var text = target.Format == McpConfigFormat.Toml
                ? TomlConfigEditor.Add(string.Empty, spec)!
                : JsonConfigEditor.Add(target, string.Empty, spec)!;
            await Assert.That(ConnectionCheck.Client(target, text, spec).Level).IsEqualTo(ConnectionCheckLevel.Ok);
            await Assert.That(ConnectionCheck.Client(target, text.Replace("local", "remote"), spec).Level).IsEqualTo(ConnectionCheckLevel.Warning);
            await Assert.That(ConnectionCheck.Client(target, text.Replace("REDACT_PATHS", "OTHER_SETTING"), spec).Level).IsEqualTo(ConnectionCheckLevel.Warning);
            await Assert.That(ConnectionCheck.Client(target, text, McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Warning);
        }
    }

    [Test]
    public async Task MissingInvalidAndForeignConfigurationsHaveAdvice()
    {
        foreach (var target in new[] { ConnectTestSupport.Desktop, ConnectTestSupport.Codex })
        {
            await Assert.That(ConnectionCheck.Client(target, string.Empty, McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Warning);
            await Assert.That(ConnectionCheck.Client(target, "broken", McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Problem);
        }
        const string foreign = "{\"mcpServers\":{\"revit-model-mcp\":{\"url\":\"https://example.com\"}}}";
        await Assert.That(ConnectionCheck.Client(ConnectTestSupport.Desktop, foreign, McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Warning);
    }

    [Test]
    public async Task JsonRejectsMissingEnvironmentExtraOverridesAndDisabledEntries()
    {
        var target = ConnectTestSupport.Desktop;
        var text = JsonConfigEditor.Add(target, string.Empty)!;
        await Assert.That(ConnectionCheck.Client(target, text.Replace("\"local\"", "\"local\",\"REVIT_MCP_TRANSPORT\":\"http\""), McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Warning);
        await Assert.That(ConnectionCheck.Client(target, text.Replace("\"command\"", "\"disabled\":true,\"command\""), McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Warning);
        await Assert.That(ConnectionCheck.Client(target, "{\"mcpServers\":{\"revit-model-mcp\":{\"command\":\"uvx\",\"args\":[\"revit-model-mcp\"]}}}", McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Warning);
    }

    [Test]
    public async Task TomlRejectsExtraEnvironmentAndDisabledEntry()
    {
        var target = ConnectTestSupport.Codex;
        var text = TomlConfigEditor.Add(string.Empty)!;
        await Assert.That(ConnectionCheck.Client(target, text + "REVIT_MCP_TRANSPORT = 'http'\n", McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Warning);
        await Assert.That(ConnectionCheck.Client(target, text.Replace("command =", "enabled = false\ncommand ="), McpServerSpec.Uvx).Level).IsEqualTo(ConnectionCheckLevel.Warning);
    }

    [Test]
    public async Task VersionIgnoresBuildMetadataButNotReleaseOrPrerelease()
    {
        await Assert.That(ConnectionCheck.VersionsMatch("2.0.0+sha", "2.0.0")).IsTrue();
        await Assert.That(ConnectionCheck.VersionsMatch("2.0.0", "2.0.1")).IsFalse();
        await Assert.That(ConnectionCheck.VersionsMatch("2.0.0", "2.0.0-rc.1")).IsFalse();
    }

    [Test]
    public async Task SuccessfulStartupDoesNotClaimRevitReachability()
    {
        var result = ConnectionCheck.ServerStarted("2.0.0+sha", Initialize, Tools);
        await Assert.That(result.Level).IsEqualTo(ConnectionCheckLevel.Ok);
        await Assert.That(result.Advice).Contains("close this window");
        await Assert.That(result.Advice).Contains("revit_ping");
    }

    [Test]
    public async Task StartupRejectsWrongVersionErrorEmptyToolsAndWrongResponseId()
    {
        await Assert.That(ConnectionCheck.ServerStarted("2.0.1", Initialize, Tools).Level).IsEqualTo(ConnectionCheckLevel.Problem);
        await Assert.That(ConnectionCheck.ServerStarted("2.0.0", "{\"id\":1,\"error\":{\"code\":-1}}", Tools).Level).IsEqualTo(ConnectionCheckLevel.Problem);
        await Assert.That(ConnectionCheck.ServerStarted("2.0.0", Initialize, "{\"id\":2,\"result\":{\"tools\":[]}}").Level).IsEqualTo(ConnectionCheckLevel.Problem);
        await Assert.That(ConnectionCheck.ServerStarted("2.0.0", Initialize, Tools.Replace("\"id\":2", "\"id\":3")).Level).IsEqualTo(ConnectionCheckLevel.Problem);
    }

    [Test]
    public async Task ProbeNeverStartsUvxOrMissingExecutable()
    {
        var uvx = await ConnectionServerProbe.CheckAsync(McpServerSpec.Uvx, "2.0.0", CancellationToken.None);
        var missing = await ConnectionServerProbe.CheckAsync(McpServerSpec.ForExecutable("missing.exe"), "2.0.0", CancellationToken.None);
        await Assert.That(uvx.Level).IsEqualTo(ConnectionCheckLevel.Warning);
        await Assert.That(missing.Level).IsEqualTo(ConnectionCheckLevel.Warning);
    }
}
