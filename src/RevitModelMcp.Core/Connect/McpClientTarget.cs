namespace RevitModelMcp.Core.Connect;

/// <summary>The configuration file format of an AI app.</summary>
public enum McpConfigFormat
{
    /// <summary>JSON, possibly with comments and trailing commas (VS Code).</summary>
    Json,

    /// <summary>TOML (Codex).</summary>
    Toml
}

/// <summary>What the add-in found out about one AI app's configuration.</summary>
public enum McpClientState
{
    /// <summary>The app's folder does not exist on this workstation.</summary>
    NotInstalled,

    /// <summary>The app is present and has no <c>revit-model-mcp</c> entry.</summary>
    NotConfigured,

    /// <summary>The app's configuration has a <c>revit-model-mcp</c> entry that starts the server this add-in resolved.</summary>
    Configured,

    /// <summary>The entry exists but starts the server with another command or arguments than this add-in resolved.</summary>
    ConfiguredDifferentCommand,

    /// <summary>The configuration cannot be read or parsed, so the add-in leaves it alone.</summary>
    Unreadable
}

/// <summary>One AI app configuration file the add-in can edit.</summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="DisplayName">Name shown in the dialog.</param>
/// <param name="InstallProbe">Folder whose existence means the app is installed.</param>
/// <param name="ConfigPath">The configuration file.</param>
/// <param name="Format">Configuration file format.</param>
/// <param name="RootKey">JSON object that holds the servers (<c>mcpServers</c> or <c>servers</c>); unused for TOML.</param>
/// <param name="IncludeType">Whether the JSON entry carries <c>"type": "stdio"</c>.</param>
public sealed record McpClientTarget(
    string Id,
    string DisplayName,
    string InstallProbe,
    string ConfigPath,
    McpConfigFormat Format,
    string RootKey = "mcpServers",
    bool IncludeType = false);

/// <summary>The inspected state of one AI app.</summary>
/// <param name="Target">The app.</param>
/// <param name="State">What was found.</param>
/// <param name="Detail">Why the configuration is unreadable; otherwise null.</param>
public sealed record McpClientStatus(McpClientTarget Target, McpClientState State, string? Detail = null);

/// <summary>The outcome of adding, updating or removing the entry.</summary>
/// <param name="Changed">False when the file already had the requested state and was not touched.</param>
/// <param name="BackupPath">The backup of the previous file, or null when none existed or nothing changed.</param>
public sealed record McpConfigChange(bool Changed, string? BackupPath);

/// <summary>Raised when the configuration cannot be changed safely.</summary>
public sealed class McpConfigException(string message) : Exception(message);
