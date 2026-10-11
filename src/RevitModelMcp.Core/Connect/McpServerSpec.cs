namespace RevitModelMcp.Core.Connect;

/// <summary>
///     The one server entry the add-in writes into AI app configurations: the launcher program and its arguments.
///     The bundled executable of the MSI needs neither uv nor Python; <c>uvx revit-model-mcp</c> is the fallback.
/// </summary>
public sealed record McpServerSpec(string Command, IReadOnlyList<string> Args)
{
    /// <summary>The key of the entry in every client configuration.</summary>
    public const string Name = "revit-model-mcp";

    /// <summary>The file name of the bundled server executable.</summary>
    public const string ExecutableName = "revit-model-mcp.exe";

    /// <summary>The documented local registration through uv.</summary>
    public static McpServerSpec Uvx { get; } = new("uvx", [Name]);

    /// <summary>The environment the server needs to talk to the Revit on this workstation.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Env { get; } =
    [
        new("REVIT_MCP_HOST", "local"),
        new("REVIT_MCP_REDACT_PATHS", "1")
    ];

    /// <summary>Runs the bundled server executable at <paramref name="path" /> directly.</summary>
    public static McpServerSpec ForExecutable(string path) => new(path, []);

    /// <summary>
    ///     Picks the first candidate path of the bundled executable that exists, or the uvx fallback.
    /// </summary>
    /// <param name="candidates">Absolute executable paths in order of preference.</param>
    /// <param name="fileExists">Tests whether a file exists.</param>
    public static McpServerSpec Resolve(IEnumerable<string> candidates, Func<string, bool> fileExists)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && fileExists(candidate)) return ForExecutable(candidate);
        }

        return Uvx;
    }

    /// <summary>
    ///     The places the MSI puts the bundled executable, for an add-in assembly in <paramref name="addinDirectory" />:
    ///     next to the add-in, then the machine-wide and per-user install folders.
    /// </summary>
    public static IReadOnlyList<string> BundledCandidates(string? addinDirectory, string? programFiles, string? localAppData)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(addinDirectory))
            candidates.Add(Combine(addinDirectory, "server", ExecutableName));
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Combine(programFiles, "Model MCP", "server", ExecutableName));
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Combine(localAppData, "Programs", "Model MCP", "server", ExecutableName));
        return candidates;
    }

    /// <summary>The Claude Code registration command, as documented in the README.</summary>
    public string ClaudeCodeCommand =>
        "claude mcp add " + Name + " " + string.Join(" ", Env.Select(pair => "-e " + pair.Key + "=" + pair.Value)) +
        " -- " + string.Join(" ", new[] { Command }.Concat(Args).Select(ShellQuote));

    /// <summary>A short label for the dialog text.</summary>
    public string Display => string.Join(" ", new[] { Command }.Concat(Args));

    // Windows separators regardless of the host, so a path stays valid in the clients' configuration files.
    private static string Combine(string first, params string[] rest) =>
        string.Join('\\', new[] { first.TrimEnd('\\', '/') }.Concat(rest));

    private static string ShellQuote(string value) =>
        value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;
}
