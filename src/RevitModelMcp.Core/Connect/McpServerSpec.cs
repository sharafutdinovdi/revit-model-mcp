namespace RevitModelMcp.Core.Connect;

/// <summary>
///     The one server entry the add-in writes into AI app configurations. It mirrors the local
///     workstation registration documented in the README and the install guide.
/// </summary>
public static class McpServerSpec
{
    /// <summary>The key of the entry in every client configuration.</summary>
    public const string Name = "revit-model-mcp";

    /// <summary>The launcher program.</summary>
    public const string Command = "uvx";

    /// <summary>The launcher arguments.</summary>
    public static IReadOnlyList<string> Args { get; } = ["revit-model-mcp"];

    /// <summary>The environment the server needs to talk to the Revit on this workstation.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Env { get; } =
    [
        new("REVIT_MCP_HOST", "local"),
        new("REVIT_MCP_REDACT_PATHS", "1")
    ];

    /// <summary>The Claude Code registration command, as documented in the README.</summary>
    public static string ClaudeCodeCommand =>
        "claude mcp add " + Name + " " + string.Join(" ", Env.Select(pair => "-e " + pair.Key + "=" + pair.Value)) +
        " -- " + Command + " " + string.Join(" ", Args);
}
