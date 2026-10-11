using RevitModelMcp.Core.Connect;

namespace RevitModelMcp.Core.Tests.Connect;

internal static class ConnectTestSupport
{
    public static readonly McpClientTarget Desktop =
        new("claude-desktop", "Claude Desktop", "x", "x", McpConfigFormat.Json);

    public static readonly McpClientTarget VsCode =
        new("vscode", "Visual Studio Code", "x", "x", McpConfigFormat.Json, "servers", IncludeType: true);

    public static readonly McpClientTarget Codex = new("codex", "Codex", "x", "x", McpConfigFormat.Toml);
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rmm-connect-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
    }
}
