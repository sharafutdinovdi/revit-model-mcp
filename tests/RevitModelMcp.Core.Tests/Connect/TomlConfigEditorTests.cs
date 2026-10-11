using RevitModelMcp.Core.Connect;
using static RevitModelMcp.Core.Tests.Connect.ConnectTestSupport;

namespace RevitModelMcp.Core.Tests.Connect;

public sealed class TomlConfigEditorTests
{
    private const string Block =
        "[mcp_servers.revit-model-mcp]\ncommand = \"uvx\"\nargs = [\"revit-model-mcp\"]\n\n[mcp_servers.revit-model-mcp.env]\nREVIT_MCP_HOST = \"local\"\nREVIT_MCP_REDACT_PATHS = \"1\"\n";

    private const string Existing = "# Codex settings\nmodel = \"gpt\"\n\n[mcp_servers.other]\ncommand = \"node\" # keep\n";

    [Test]
    public async Task EmptyTextGetsTheBlock()
    {
        await Assert.That(TomlConfigEditor.Inspect(Codex, "").State).IsEqualTo(McpClientState.NotConfigured);
        await Assert.That(TomlConfigEditor.Add("")).IsEqualTo(Block);
    }

    [Test]
    public async Task AddAppendsAndKeepsCommentsAndOtherTables()
    {
        var updated = TomlConfigEditor.Add(Existing)!;
        await Assert.That(updated).IsEqualTo(Existing + "\n" + Block);
        await Assert.That(TomlConfigEditor.Inspect(Codex, updated).State).IsEqualTo(McpClientState.Configured);
    }

    [Test]
    public async Task AddHandlesMissingFinalNewlineAndCrLf()
    {
        var updated = TomlConfigEditor.Add("model = \"gpt\"")!;
        await Assert.That(updated).IsEqualTo("model = \"gpt\"\n\n" + Block);
        var crlf = TomlConfigEditor.Add("model = \"gpt\"\r\n")!;
        await Assert.That(crlf).IsEqualTo("model = \"gpt\"\r\n\r\n" + Block.Replace("\n", "\r\n"));
    }

    [Test]
    public async Task RemoveRestoresTheOriginalText()
    {
        await Assert.That(TomlConfigEditor.Remove(TomlConfigEditor.Add(Existing)!)).IsEqualTo(Existing);
        await Assert.That(TomlConfigEditor.Remove(TomlConfigEditor.Add("")!)).IsEqualTo("");
    }

    [Test]
    public async Task RemoveInTheMiddleKeepsTheNextTableAndItsComment()
    {
        var text = "[a]\nx = 1\n\n" + Block + "\n# about b\n[b]\ny = 2\n";
        await Assert.That(TomlConfigEditor.Remove(text)).IsEqualTo("[a]\nx = 1\n\n# about b\n[b]\ny = 2\n");
    }

    [Test]
    public async Task QuotedKeyAndTrailingCommentHeaderAreRecognised()
    {
        var text = "[mcp_servers.\"revit-model-mcp\"] # ours\ncommand = \"x\"\n[mcp_servers.other]\na = 1\n";
        await Assert.That(TomlConfigEditor.Inspect(Codex, text).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(TomlConfigEditor.Remove(text)).IsEqualTo("[mcp_servers.other]\na = 1\n");
        await Assert.That(TomlConfigEditor.Add(text)).IsNull();
    }

    [Test]
    public async Task AddAndRemoveReturnNullWhenNothingToDo()
    {
        await Assert.That(TomlConfigEditor.Remove(Existing)).IsNull();
        await Assert.That(TomlConfigEditor.Remove("")).IsNull();
        await Assert.That(TomlConfigEditor.Add(Block)).IsNull();
    }

    [Test]
    public async Task OtherServerWithSimilarNameIsNotOurs()
    {
        const string text = "[mcp_servers.revit-model-mcp-dev]\ncommand = \"x\"\n[mcp_servers.other.revit-model-mcp]\na = 1\n";
        await Assert.That(TomlConfigEditor.Inspect(Codex, text).State).IsEqualTo(McpClientState.NotConfigured);
    }

    [Test]
    public async Task MultilineValuesWithBracketsAreNotHeaders()
    {
        const string text = "notes = \"\"\"\n[not.a.header]\nbroken line\n\"\"\"\nitems = [\n  \"a\",\n  \"b\",\n]\ntable = { x = 1 }\n[mcp_servers.other]\ncommand = \"c\"\n";
        await Assert.That(TomlConfigEditor.Inspect(Codex, text).State).IsEqualTo(McpClientState.NotConfigured);
        var updated = TomlConfigEditor.Add(text)!;
        await Assert.That(TomlConfigEditor.Remove(updated)).IsEqualTo(text);
    }

    [Test]
    public async Task OurEnvTableMayBeSeparatedFromTheMainTable()
    {
        const string text = "[mcp_servers.revit-model-mcp]\ncommand = \"x\"\n[other]\na = 1\n[mcp_servers.revit-model-mcp.env]\nK = \"v\"\n";
        await Assert.That(TomlConfigEditor.Remove(text)).IsEqualTo("[other]\na = 1\n");
    }

    [Test]
    [Arguments("[broken\nx = 1\n")]
    [Arguments("[a]\nthis is not toml\n")]
    [Arguments("x = \"\"\"never closed\n")]
    [Arguments("[a]\nitems = [\n  1,\n")]
    [Arguments("[[a]\n")]
    [Arguments("mcp_servers.revit-model-mcp.command = \"x\"\n")]
    [Arguments("mcp_servers = { revit-model-mcp = { command = \"x\" } }\n")]
    [Arguments("[mcp_servers]\nrevit-model-mcp = { command = \"x\" }\n")]
    [Arguments("[mcp_servers]\nrevit-model-mcp.command = \"x\"\n")]
    public async Task MalformedOrUnsupportedLayoutIsUnreadableAndNeverEdited(string text)
    {
        var status = TomlConfigEditor.Inspect(Codex, text);
        await Assert.That(status.State).IsEqualTo(McpClientState.Unreadable);
        await Assert.That(status.Detail).IsNotNull();
        Assert.Throws<McpConfigException>(() => TomlConfigEditor.Add(text));
        Assert.Throws<McpConfigException>(() => TomlConfigEditor.Remove(text));
    }
}
