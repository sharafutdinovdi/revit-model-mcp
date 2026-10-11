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
}
