using RevitModelMcp.Core.Connect;

namespace RevitModelMcp.Core.Tests.Connect;

public sealed class McpClientConfiguratorTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 20, 30, 15);

    private static McpClientTarget Json(TempDirectory temp) => new("cursor", "Cursor", temp.Combine(".cursor"),
        temp.Combine(".cursor", "mcp.json"), McpConfigFormat.Json);

    private static McpClientTarget Toml(TempDirectory temp) => new("codex", "Codex", temp.Combine(".codex"),
        temp.Combine(".codex", "config.toml"), McpConfigFormat.Toml);

    [Test]
    public async Task MissingFolderIsNotInstalledAndAddRefuses()
    {
        using var temp = new TempDirectory();
        var configurator = new McpClientConfigurator(() => Now);
        await Assert.That(configurator.Inspect(Json(temp)).State).IsEqualTo(McpClientState.NotInstalled);
        Assert.Throws<McpConfigException>(() => configurator.Add(Json(temp)));
        await Assert.That(Directory.Exists(temp.Combine(".cursor"))).IsFalse();
    }

    [Test]
    public async Task InstalledWithoutFileIsNotConfiguredAndAddCreatesIt()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var target = Json(temp);
        var configurator = new McpClientConfigurator(() => Now);
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.NotConfigured);
        var change = configurator.Add(target);
        await Assert.That(change.Changed).IsTrue();
        await Assert.That(change.BackupPath).IsNull();
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(configurator.Remove(target).Changed).IsTrue();
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.NotConfigured);
    }

    [Test]
    public async Task ChangingAnExistingFileWritesATimestampedBackupOfTheOldContent()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".codex"));
        var target = Toml(temp);
        const string original = "# mine\nmodel = \"x\"\n";
        File.WriteAllText(target.ConfigPath, original);
        var configurator = new McpClientConfigurator(() => Now);
        var change = configurator.Add(target);
        await Assert.That(change.BackupPath).IsEqualTo(target.ConfigPath + ".20261010-203015.bak");
        await Assert.That(File.ReadAllText(change.BackupPath!)).IsEqualTo(original);
        await Assert.That(File.ReadAllText(target.ConfigPath).StartsWith(original, StringComparison.Ordinal)).IsTrue();
        var second = configurator.Remove(target);
        await Assert.That(second.BackupPath).IsEqualTo(target.ConfigPath + ".20261010-203015-2.bak");
        await Assert.That(File.ReadAllText(target.ConfigPath)).IsEqualTo(original);
        await Assert.That(Directory.GetFiles(temp.Combine(".codex")).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task NoOpDoesNotTouchOrBackUpTheFile()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var target = Json(temp);
        File.WriteAllText(target.ConfigPath, "{}");
        var configurator = new McpClientConfigurator(() => Now);
        var change = configurator.Remove(target);
        await Assert.That(change.Changed).IsFalse();
        await Assert.That(Directory.GetFiles(temp.Combine(".cursor")).Length).IsEqualTo(1);
    }

    [Test]
    public async Task UnreadableFileIsReportedAndLeftByteForByteAlone()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var target = Json(temp);
        File.WriteAllText(target.ConfigPath, "{ broken");
        var configurator = new McpClientConfigurator(() => Now);
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.Unreadable);
        Assert.Throws<McpConfigException>(() => configurator.Add(target));
        await Assert.That(File.ReadAllText(target.ConfigPath)).IsEqualTo("{ broken");
        await Assert.That(Directory.GetFiles(temp.Combine(".cursor")).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Utf8BomIsPreserved()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var target = Json(temp);
        File.WriteAllBytes(target.ConfigPath, [0xEF, 0xBB, 0xBF, .. "{\"a\": 1}"u8.ToArray()]);
        var configurator = new McpClientConfigurator(() => Now);
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.NotConfigured);
        configurator.Add(target);
        var bytes = File.ReadAllBytes(target.ConfigPath);
        await Assert.That(bytes[0]).IsEqualTo((byte)0xEF);
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.Configured);
    }

    private const string BundledPath = @"C:\Program Files\Model MCP\server\revit-model-mcp.exe";

    private static readonly McpServerSpec Bundled = McpServerSpec.ForExecutable(BundledPath);

    [Test]
    public async Task JsonEntryWithAnotherCommandIsDifferentAndUpdateReplacesOnlyIt()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var target = Json(temp);
        const string original =
            "{\n  // mine\n  \"theme\": \"dark\",\n  \"mcpServers\": {\n    \"other\": { \"command\": \"npx\", \"args\": [\"x\"] },\n" +
            "    \"revit-model-mcp\": { \"command\": \"uvx\", \"args\": [\"revit-model-mcp==0.6.0\"] }\n  }\n}\n";
        File.WriteAllText(target.ConfigPath, original);
        var configurator = new McpClientConfigurator(() => Now, Bundled);
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.ConfiguredDifferentCommand);
        await Assert.That(configurator.Add(target).Changed).IsFalse();

        var change = configurator.Update(target);
        await Assert.That(change.Changed).IsTrue();
        await Assert.That(File.ReadAllText(change.BackupPath!)).IsEqualTo(original);
        var text = File.ReadAllText(target.ConfigPath);
        await Assert.That(text).Contains("// mine");
        await Assert.That(text).Contains("\"other\": { \"command\": \"npx\", \"args\": [\"x\"] }");
        await Assert.That(text).Contains(BundledPath.Replace("\\", "\\\\"));
        await Assert.That(text).DoesNotContain("0.6.0");
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(configurator.Update(target).Changed).IsFalse();
    }

    [Test]
    public async Task TomlEntryWithAnotherCommandIsDifferentAndUpdateReplacesOnlyIt()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".codex"));
        var target = Toml(temp);
        const string original =
            "# mine\nmodel = \"x\"\n\n[mcp_servers.other]\ncommand = \"npx\"\nargs = [\"x\"]\n\n" +
            "[mcp_servers.revit-model-mcp]\ncommand = \"uvx\"\nargs = [\"revit-model-mcp==0.6.0\"]\n\n" +
            "[mcp_servers.revit-model-mcp.env]\nREVIT_MCP_HOST = \"local\"\n\n[tui]\nnotifications = true\n";
        File.WriteAllText(target.ConfigPath, original);
        var configurator = new McpClientConfigurator(() => Now, Bundled);
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.ConfiguredDifferentCommand);

        var change = configurator.Update(target);
        await Assert.That(change.Changed).IsTrue();
        await Assert.That(File.ReadAllText(change.BackupPath!)).IsEqualTo(original);
        var text = File.ReadAllText(target.ConfigPath);
        await Assert.That(text).Contains("# mine\nmodel = \"x\"");
        await Assert.That(text).Contains("[mcp_servers.other]\ncommand = \"npx\"\nargs = [\"x\"]");
        await Assert.That(text).Contains("[tui]\nnotifications = true");
        await Assert.That(text).DoesNotContain("0.6.0");
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(configurator.Update(target).Changed).IsFalse();
    }

    [Test]
    public async Task UpdateDoesNothingWithoutAnEntryOrFile()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var target = Json(temp);
        var configurator = new McpClientConfigurator(() => Now, Bundled);
        await Assert.That(configurator.Update(target).Changed).IsFalse();
        await Assert.That(File.Exists(target.ConfigPath)).IsFalse();
        File.WriteAllText(target.ConfigPath, "{ \"mcpServers\": {} }");
        await Assert.That(configurator.Update(target).Changed).IsFalse();
    }

    [Test]
    public async Task TomlLiteralStringAndMultilineArgsAreRecognizedAsTheSameCommand()
    {
        var spec = McpServerSpec.Uvx;
        var text = "[mcp_servers.revit-model-mcp]\ncommand = 'uvx'\nargs = [\n  \"revit-model-mcp\", # pinned by name\n]\n";
        await Assert.That(TomlConfigEditor.Inspect(ConnectTestSupport.Codex, text, spec).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(TomlConfigEditor.Inspect(ConnectTestSupport.Codex, text, Bundled).State)
            .IsEqualTo(McpClientState.ConfiguredDifferentCommand);
    }

    [Test]
    public async Task JsonEntryWithoutCommandCountsAsDifferent()
    {
        const string text = "{ \"mcpServers\": { \"revit-model-mcp\": { \"url\": \"http://x\" } } }";
        await Assert.That(JsonConfigEditor.Inspect(ConnectTestSupport.Desktop, text, McpServerSpec.Uvx).State)
            .IsEqualTo(McpClientState.ConfiguredDifferentCommand);
        await Assert.That(JsonConfigEditor.Inspect(ConnectTestSupport.Desktop, text).State).IsEqualTo(McpClientState.Configured);
    }
}
