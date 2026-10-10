using RevitModelMcp.Core.Connect;

namespace RevitModelMcp.Core.Tests.Connect;

public sealed class McpClientLocatorTests
{
    [Test]
    public async Task ListsClassicClientsInDialogOrder()
    {
        using var temp = new TempDirectory();
        var targets = McpClientLocator.Discover(temp.Combine("Roaming"), temp.Combine("Local"), temp.Combine("home"));
        await Assert.That(targets.Select(target => target.Id).ToArray())
            .IsEquivalentTo(new[] { "claude-desktop", "cursor", "vscode", "codex" });
        await Assert.That(targets[0].ConfigPath).IsEqualTo(temp.Combine("Roaming", "Claude", "claude_desktop_config.json"));
        await Assert.That(targets[1].ConfigPath).IsEqualTo(temp.Combine("home", ".cursor", "mcp.json"));
        await Assert.That(targets[2].ConfigPath).IsEqualTo(temp.Combine("Roaming", "Code", "User", "mcp.json"));
        await Assert.That(targets[2].RootKey).IsEqualTo("servers");
        await Assert.That(targets[2].IncludeType).IsTrue();
        await Assert.That(targets[3].ConfigPath).IsEqualTo(temp.Combine("home", ".codex", "config.toml"));
        await Assert.That(targets[3].Format).IsEqualTo(McpConfigFormat.Toml);
    }

    [Test]
    public async Task AddsTheMicrosoftStorePackageOnlyWhenItExists()
    {
        using var temp = new TempDirectory();
        var package = temp.Combine("Local", "Packages", "Claude_pzs8sxrjxfjjc");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(temp.Combine("Local", "Packages", "Other_abc"));
        var targets = McpClientLocator.Discover(temp.Combine("Roaming"), temp.Combine("Local"), temp.Combine("home"));
        var store = targets.Single(target => target.Id == "claude-desktop-store");
        await Assert.That(store.InstallProbe).IsEqualTo(package);
        await Assert.That(store.ConfigPath).IsEqualTo(Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
    }
}
