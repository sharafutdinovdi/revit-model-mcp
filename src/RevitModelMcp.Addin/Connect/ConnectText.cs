using RevitModelMcp.Core.Connect;

namespace RevitModelMcp.Connect;

/// <summary>Connect AI app ribbon and dialog text; English in every Revit UI language.</summary>
internal static class ConnectText
{
    public const string RibbonButton = "Connect AI app";
    public const string RibbonToolTip =
        "Add or remove the Revit Model MCP server in Claude Desktop, Cursor, Visual Studio Code and Codex on this computer, or copy the Claude Code command.";
    public const string Title = "Connect AI app";

    public static string Intro(McpServerSpec spec) =>
        $"Adds the \"revit-model-mcp\" server ({spec.Display}, this Revit workstation) to an app's MCP settings. " +
        "Only that entry is changed, and the previous file is saved next to it first. Restart the app afterwards. " +
        (spec == McpServerSpec.Uvx
            ? "The apps start the server with uv, so uv must be installed."
            : "The apps start the server installed with this add-in, so neither uv nor Python is needed.");

    public const string BundledServerMissing = "The bundled server was not found. Reinstall Model MCP.";

    public const string ClaudeCode = "Claude Code";
    public const string ClaudeCodeHint = "Run this in a terminal.";
    public const string CopyCommand = "Copy command";
    public const string Copied = "Command copied.";
    public const string CopyFailed = "The clipboard is busy. Select the command and copy it by hand.";
    public const string Add = "Add";
    public const string Update = "Update";
    public const string Remove = "Remove";
    public const string Close = "Close";
    public const string NotInstalled = "Not installed";
    public const string NotConfigured = "Not configured";
    public const string Configured = "Configured";
    public const string ConfiguredDifferentCommand = "Configured (different command)";
    public const string Unreadable = "Config unreadable";

    public static string State(McpClientState state) => state switch
    {
        McpClientState.NotInstalled => NotInstalled,
        McpClientState.NotConfigured => NotConfigured,
        McpClientState.Configured => Configured,
        McpClientState.ConfiguredDifferentCommand => ConfiguredDifferentCommand,
        _ => Unreadable
    };

    public static string Added(string app, string? backup) =>
        $"Added to {app}. Restart {app} to load it." + BackupNote(backup);

    public static string Removed(string app, string? backup) =>
        $"Removed from {app}. Restart {app} to apply it." + BackupNote(backup);

    public static string Updated(string app, string? backup) =>
        $"Updated in {app}. Restart {app} to load it." + BackupNote(backup);

    public static string Unchanged(string app) => $"{app} already had that state; nothing was changed.";

    public static string Failed(string app, string reason) => $"{app}: {reason}";

    private static string BackupNote(string? backup) => backup is null ? string.Empty : $" Backup: {backup}";
}
