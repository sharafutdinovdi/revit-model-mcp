namespace RevitModelMcp.Core.Connect;

/// <summary>Finds the configuration files of the supported AI apps on a workstation.</summary>
public static class McpClientLocator
{
    /// <summary>Lists the apps in dialog order. Folders are only probed, never created.</summary>
    /// <param name="appData">The roaming application data folder (<c>%APPDATA%</c>).</param>
    /// <param name="localAppData">The local application data folder (<c>%LOCALAPPDATA%</c>).</param>
    /// <param name="home">The user profile folder.</param>
    public static IReadOnlyList<McpClientTarget> Discover(string appData, string localAppData, string home)
    {
        var targets = new List<McpClientTarget>
        {
            new("claude-desktop", "Claude Desktop", Path.Combine(appData, "Claude"),
                Path.Combine(appData, "Claude", "claude_desktop_config.json"), McpConfigFormat.Json)
        };
        foreach (var package in StorePackages(localAppData))
        {
            var roaming = Path.Combine(package, "LocalCache", "Roaming", "Claude");
            targets.Add(new McpClientTarget("claude-desktop-store", "Claude Desktop (Microsoft Store)", package,
                Path.Combine(roaming, "claude_desktop_config.json"), McpConfigFormat.Json));
        }
        targets.Add(new McpClientTarget("cursor", "Cursor", Path.Combine(home, ".cursor"),
            Path.Combine(home, ".cursor", "mcp.json"), McpConfigFormat.Json));
        targets.Add(new McpClientTarget("vscode", "Visual Studio Code", Path.Combine(appData, "Code", "User"),
            Path.Combine(appData, "Code", "User", "mcp.json"), McpConfigFormat.Json, "servers", IncludeType: true));
        targets.Add(new McpClientTarget("codex", "Codex", Path.Combine(home, ".codex"),
            Path.Combine(home, ".codex", "config.toml"), McpConfigFormat.Toml));
        return targets;
    }

    private static IEnumerable<string> StorePackages(string localAppData)
    {
        var packages = Path.Combine(localAppData, "Packages");
        try
        {
            if (!Directory.Exists(packages)) return [];
            return Directory.GetDirectories(packages, "Claude_*").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
