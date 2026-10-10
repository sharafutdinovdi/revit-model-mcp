using RevitModelMcp.Core.Connect;

namespace RevitModelMcp.Core.Tests.Connect;

public sealed class McpServerSpecTests
{
    [Test]
    public async Task ClaudeCodeCommandMatchesTheDocumentedLocalRegistration() =>
        await Assert.That(McpServerSpec.ClaudeCodeCommand)
            .IsEqualTo("claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- uvx revit-model-mcp");
}
