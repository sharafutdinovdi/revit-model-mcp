using RevitModelMcp.Core.Connect;
using static RevitModelMcp.Core.Tests.Connect.ConnectTestSupport;

namespace RevitModelMcp.Core.Tests.Connect;

public sealed class JsonConfigEditorTests
{
    private const string Existing = """
        {
          "mcpServers": {
            "other": {
              "command": "node"
            }
          },
          "theme": "dark"
        }
        """;

    [Test]
    public async Task EmptyTextIsNotConfiguredAndAddCreatesAValidFile()
    {
        await Assert.That(JsonConfigEditor.Inspect(Desktop, "").State).IsEqualTo(McpClientState.NotConfigured);
        var created = JsonConfigEditor.Add(Desktop, "")!;
        await Assert.That(JsonConfigEditor.Inspect(Desktop, created).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(created).IsEqualTo("""
            {
              "mcpServers": {
                "revit-model-mcp": {
                  "command": "uvx",
                  "args": ["revit-model-mcp"],
                  "env": {
                    "REVIT_MCP_HOST": "local",
                    "REVIT_MCP_REDACT_PATHS": "1"
                  }
                }
              }
            }

            """);
    }

    [Test]
    public async Task AddKeepsUnknownKeysAndSiblingServers()
    {
        var updated = JsonConfigEditor.Add(Desktop, Existing)!;
        await Assert.That(updated).IsEqualTo("""
            {
              "mcpServers": {
                "other": {
                  "command": "node"
                },
                "revit-model-mcp": {
                  "command": "uvx",
                  "args": ["revit-model-mcp"],
                  "env": {
                    "REVIT_MCP_HOST": "local",
                    "REVIT_MCP_REDACT_PATHS": "1"
                  }
                }
              },
              "theme": "dark"
            }
            """);
        await Assert.That(JsonConfigEditor.Inspect(Desktop, updated).State).IsEqualTo(McpClientState.Configured);
    }

    [Test]
    public async Task RemoveRestoresTheOriginalText()
    {
        var added = JsonConfigEditor.Add(Desktop, Existing)!;
        await Assert.That(JsonConfigEditor.Remove(Desktop, added)).IsEqualTo(Existing);
    }

    [Test]
    public async Task AddWhenEntryExistsAndRemoveWhenMissingReturnNull()
    {
        var added = JsonConfigEditor.Add(Desktop, Existing)!;
        await Assert.That(JsonConfigEditor.Add(Desktop, added)).IsNull();
        await Assert.That(JsonConfigEditor.Remove(Desktop, Existing)).IsNull();
        await Assert.That(JsonConfigEditor.Remove(Desktop, "")).IsNull();
        await Assert.That(JsonConfigEditor.Remove(Desktop, "{}")).IsNull();
    }

    [Test]
    public async Task MissingRootKeyIsAddedToTheTopLevelObject()
    {
        var updated = JsonConfigEditor.Add(Desktop, "{\n  \"theme\": \"dark\"\n}")!;
        await Assert.That(updated.StartsWith("{\n  \"theme\": \"dark\",\n  \"mcpServers\": {\n    \"revit-model-mcp\": {\n", StringComparison.Ordinal)).IsTrue();
        await Assert.That(JsonConfigEditor.Inspect(Desktop, updated).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(JsonConfigEditor.Remove(Desktop, updated)!.Contains("\"theme\": \"dark\"", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task EmptyObjectsGetTheEntry()
    {
        foreach (var text in new[] { "{}", "{ }", "{\"mcpServers\": {}}", "{\n  \"mcpServers\": {\n  }\n}" })
        {
            var updated = JsonConfigEditor.Add(Desktop, text)!;
            await Assert.That(JsonConfigEditor.Inspect(Desktop, updated).State).IsEqualTo(McpClientState.Configured);
        }
    }

    [Test]
    public async Task CommentsAndTrailingCommasSurviveInVsCodeConfiguration()
    {
        const string text = "// my servers\n{\n  /* inputs */\n  \"inputs\": [],\n  \"servers\": {\n    // keep me\n    \"other\": { \"command\": \"x\" }, // trailing note\n  },\n}\n";
        var updated = JsonConfigEditor.Add(VsCode, text)!;
        await Assert.That(updated.Contains("// my servers", StringComparison.Ordinal)).IsTrue();
        await Assert.That(updated.Contains("/* inputs */", StringComparison.Ordinal)).IsTrue();
        await Assert.That(updated.Contains("// keep me", StringComparison.Ordinal)).IsTrue();
        await Assert.That(updated.Contains("\"other\": { \"command\": \"x\" }, // trailing note\n    \"revit-model-mcp\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(updated.Contains("\"type\": \"stdio\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(JsonConfigEditor.Inspect(VsCode, updated).State).IsEqualTo(McpClientState.Configured);
        await Assert.That(JsonConfigEditor.Remove(VsCode, updated)).IsEqualTo(text);
    }

    [Test]
    public async Task OnlyTheOwnEntryIsRemovedWhenItIsNotLast()
    {
        const string text = "{\n  \"mcpServers\": {\n    \"revit-model-mcp\": {\"command\": \"x\"},\n    \"other\": {\"command\": \"y\"}\n  }\n}";
        await Assert.That(JsonConfigEditor.Remove(Desktop, text)).IsEqualTo("{\n  \"mcpServers\": {\n    \"other\": {\"command\": \"y\"}\n  }\n}");
    }

    [Test]
    public async Task RemovingTheLastMemberDropsThePrecedingComma()
    {
        const string text = "{\n  \"mcpServers\": {\n    \"other\": {\"command\": \"y\"},\n    \"revit-model-mcp\": {\"command\": \"x\"}\n  }\n}";
        var updated = JsonConfigEditor.Remove(Desktop, text)!;
        await Assert.That(updated).IsEqualTo("{\n  \"mcpServers\": {\n    \"other\": {\"command\": \"y\"}\n  }\n}");
    }

    [Test]
    public async Task CrLfAndTabIndentationArePreserved()
    {
        const string text = "{\r\n\t\"mcpServers\": {\r\n\t\t\"other\": {}\r\n\t}\r\n}";
        var updated = JsonConfigEditor.Add(Desktop, text)!;
        await Assert.That(updated.Replace("\r\n", "").Contains('\n')).IsFalse();
        await Assert.That(updated.Contains("\r\n\t\t\"revit-model-mcp\": {\r\n\t\t\t\"command\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(JsonConfigEditor.Remove(Desktop, updated)).IsEqualTo(text);
    }

    [Test]
    [Arguments("{")]
    [Arguments("{\"mcpServers\": }")]
    [Arguments("[1, 2]")]
    [Arguments("not json")]
    [Arguments("{\"a\": \"unterminated}")]
    [Arguments("{\"mcpServers\": {}} trailing")]
    [Arguments("{\"mcpServers\": []}")]
    [Arguments("{\"mcpServers\": {\"a\": tru}}")]
    public async Task MalformedOrWrongShapeIsUnreadableAndNeverEdited(string text)
    {
        await Assert.That(JsonConfigEditor.Inspect(Desktop, text).State).IsEqualTo(McpClientState.Unreadable);
        await Assert.That(JsonConfigEditor.Inspect(Desktop, text).Detail).IsNotNull();
        Assert.Throws<McpConfigException>(() => JsonConfigEditor.Add(Desktop, text));
        Assert.Throws<McpConfigException>(() => JsonConfigEditor.Remove(Desktop, text));
    }

    [Test]
    public async Task EntryNameInsideAStringOrOtherKeyIsNotMistakenForTheEntry()
    {
        const string text = "{\"mcpServers\": {\"other\": {\"note\": \"revit-model-mcp\"}}, \"x\": {\"revit-model-mcp\": 1}}";
        await Assert.That(JsonConfigEditor.Inspect(Desktop, text).State).IsEqualTo(McpClientState.NotConfigured);
    }

    [Test]
    public async Task EscapedAndUnicodeContentParses()
    {
        const string text = "{\"mcpServers\": {\"a\\u0062\": {\"p\": \"C:\\\\x \\\"q\\\"\", \"n\": -1.5e3, \"t\": [true, null, false]}}}";
        await Assert.That(JsonConfigEditor.Inspect(Desktop, text).State).IsEqualTo(McpClientState.NotConfigured);
    }
}
