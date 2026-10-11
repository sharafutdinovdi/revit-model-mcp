using RevitModelMcp.Core.Connect;
using static RevitModelMcp.Core.Tests.Connect.ConnectTestSupport;

namespace RevitModelMcp.Core.Tests.Connect;

public sealed class McpServerSpecTests
{
    private const string BundledPath = @"C:\Program Files\Model MCP\server\revit-model-mcp.exe";

    [Test]
    public async Task ClaudeCodeCommandMatchesTheDocumentedLocalRegistration() =>
        await Assert.That(McpServerSpec.Uvx.ClaudeCodeCommand)
            .IsEqualTo("claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- uvx revit-model-mcp");

    [Test]
    public async Task ClaudeCodeCommandQuotesTheBundledPathWithSpaces() =>
        await Assert.That(McpServerSpec.ForExecutable(BundledPath).ClaudeCodeCommand)
            .IsEqualTo("claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- \"" + BundledPath + "\"");

    [Test]
    public async Task ResolvePrefersTheFirstExistingBundledExecutable()
    {
        var spec = McpServerSpec.Resolve([@"C:\a\revit-model-mcp.exe", @"C:\b\revit-model-mcp.exe", @"C:\c\revit-model-mcp.exe"],
            path => path != @"C:\a\revit-model-mcp.exe");
        await Assert.That(spec.Command).IsEqualTo(@"C:\b\revit-model-mcp.exe");
        await Assert.That(spec.Args).IsEmpty();
    }

    [Test]
    public async Task ResolveFallsBackToUvxWhenNothingIsBundled()
    {
        await Assert.That(McpServerSpec.Resolve(["x.exe"], _ => false)).IsEqualTo(McpServerSpec.Uvx);
        await Assert.That(McpServerSpec.Resolve([], _ => true)).IsEqualTo(McpServerSpec.Uvx);
        await Assert.That(McpServerSpec.Uvx.Command).IsEqualTo("uvx");
    }

    [Test]
    public async Task ResolveSkipsBlankCandidates() =>
        await Assert.That(McpServerSpec.Resolve([" ", ""], _ => true)).IsEqualTo(McpServerSpec.Uvx);

    [Test]
    public async Task BundledCandidatesListAddinThenMachineThenUserFolders()
    {
        var candidates = McpServerSpec.BundledCandidates(
            @"C:\ProgramData\Autodesk\Revit\Addins\2025\RevitModelMcp", @"C:\Program Files", @"C:\Users\Ann\AppData\Local");
        await Assert.That(candidates).IsEquivalentTo(
        [
            @"C:\ProgramData\Autodesk\Revit\Addins\2025\RevitModelMcp\server\revit-model-mcp.exe",
            @"C:\Program Files\Model MCP\server\revit-model-mcp.exe",
            @"C:\Users\Ann\AppData\Local\Programs\Model MCP\server\revit-model-mcp.exe"
        ]);
    }

    [Test]
    public async Task BundledCandidatesTolerateTrailingSeparatorsAndMissingRoots()
    {
        await Assert.That(McpServerSpec.BundledCandidates(@"C:\x\", null, "")).IsEquivalentTo([@"C:\x\server\revit-model-mcp.exe"]);
        await Assert.That(McpServerSpec.BundledCandidates(null, null, null)).IsEmpty();
    }

    [Test]
    public async Task JsonEntryUsesTheBundledExecutableWithEscapedWindowsPath()
    {
        var text = JsonConfigEditor.Add(Desktop, "", McpServerSpec.ForExecutable(BundledPath))!;
        await Assert.That(text).Contains(@"""command"": ""C:\\Program Files\\Model MCP\\server\\revit-model-mcp.exe""");
        await Assert.That(text).Contains(@"""args"": []");
        await Assert.That(text).Contains(@"""REVIT_MCP_HOST"": ""local""");
        await Assert.That(JsonConfigEditor.Inspect(Desktop, text).State).IsEqualTo(McpClientState.Configured);
    }

    [Test]
    public async Task TomlEntryUsesTheBundledExecutableWithEscapedWindowsPath()
    {
        var text = TomlConfigEditor.Add("", McpServerSpec.ForExecutable(BundledPath))!;
        await Assert.That(text).IsEqualTo(
            "[mcp_servers.revit-model-mcp]\ncommand = \"C:\\\\Program Files\\\\Model MCP\\\\server\\\\revit-model-mcp.exe\"\nargs = []\n\n" +
            "[mcp_servers.revit-model-mcp.env]\nREVIT_MCP_HOST = \"local\"\nREVIT_MCP_REDACT_PATHS = \"1\"\n");
        await Assert.That(TomlConfigEditor.Inspect(Codex, text).State).IsEqualTo(McpClientState.Configured);
    }

    [Test]
    public async Task ConfiguratorWritesTheChosenSpecAndRemovesIt()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".cursor"));
        var target = new McpClientTarget("cursor", "Cursor", temp.Combine(".cursor"), temp.Combine(".cursor", "mcp.json"),
            McpConfigFormat.Json);
        var configurator = new McpClientConfigurator(spec: McpServerSpec.ForExecutable(BundledPath));
        await Assert.That(configurator.Add(target).Changed).IsTrue();
        await Assert.That(File.ReadAllText(target.ConfigPath)).Contains(@"C:\\Program Files\\Model MCP\\server\\revit-model-mcp.exe");
        await Assert.That(configurator.Remove(target).Changed).IsTrue();
        await Assert.That(configurator.Inspect(target).State).IsEqualTo(McpClientState.NotConfigured);
    }
}
