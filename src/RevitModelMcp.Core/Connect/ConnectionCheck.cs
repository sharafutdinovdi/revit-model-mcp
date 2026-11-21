using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RevitModelMcp.Core.Connect;

public enum ConnectionCheckLevel { Ok, Warning, Problem }

public sealed record ConnectionCheckResult(string Step, ConnectionCheckLevel Level, string Advice)
{
    public override string ToString() => $"{Level.ToString().ToLowerInvariant()}: {Step}: {Advice}";
}

public static class ConnectionCheck
{
    public static ConnectionCheckResult LocalChannel(bool listening) => listening
        ? new("This Revit", ConnectionCheckLevel.Ok, "The add-in is loaded and its local channel is available.")
        : new("This Revit", ConnectionCheckLevel.Problem, "The add-in is loaded but its local channel is unavailable; use Repair channel permissions or restart Revit.");

    public static ConnectionCheckResult Server(McpServerSpec? spec, bool exists) => spec is null
        ? new("Server", ConnectionCheckLevel.Problem, "The bundled server is missing; reinstall Model MCP.")
        : spec == McpServerSpec.Uvx
            ? new("Server", ConnectionCheckLevel.Warning, "The uvx version cannot be checked offline; install the bundled server for an offline check.")
            : exists
                ? new("Server file", ConnectionCheckLevel.Ok, "The bundled executable exists; its version is checked when it starts below.")
                : new("Server file", ConnectionCheckLevel.Problem, "The configured executable is missing; reinstall Model MCP.");

    public static IReadOnlyList<ConnectionCheckResult> Clients(IEnumerable<McpClientTarget> targets, McpServerSpec? spec)
    {
        var results = new List<ConnectionCheckResult>();
        foreach (var target in targets.Where(target => Directory.Exists(target.InstallProbe)))
        {
            try
            {
                var text = File.Exists(target.ConfigPath) ? File.ReadAllText(target.ConfigPath) : string.Empty;
                results.Add(spec is null
                    ? new(target.DisplayName, ConnectionCheckLevel.Warning, "Reinstall the missing bundled server before comparing this entry.")
                    : Client(target, text, spec));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                results.Add(new(target.DisplayName, ConnectionCheckLevel.Problem, "The configuration cannot be read; check its permissions."));
            }
        }
        return results;
    }

    public static ConnectionCheckResult Client(McpClientTarget target, string text, McpServerSpec spec)
    {
        var status = target.Format == McpConfigFormat.Toml
            ? TomlConfigEditor.CheckSettings(target, text, spec)
            : JsonConfigEditor.CheckSettings(target, text, spec);
        return status.State switch
        {
            McpClientState.Configured => new(target.DisplayName, ConnectionCheckLevel.Ok, "The configuration parses and its server, local host and redaction settings match."),
            McpClientState.NotConfigured => new(target.DisplayName, ConnectionCheckLevel.Warning, "The Model MCP entry is missing; choose Add if you want to connect this app."),
            McpClientState.Unreadable => new(target.DisplayName, ConnectionCheckLevel.Problem, "The configuration cannot be parsed safely; fix it manually before connecting."),
            _ => new(target.DisplayName, ConnectionCheckLevel.Warning, "The entry has stale or different settings; review it before choosing Update.")
        };
    }

    public static bool VersionsMatch(string expected, string actual) =>
        string.Equals(expected.Split('+')[0], actual.Split('+')[0], StringComparison.Ordinal);

    public static XElement ParseResponse(string text)
    {
        using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(text), XmlDictionaryReaderQuotas.Max);
        return XElement.Load(reader);
    }

    public static ConnectionCheckResult ServerStarted(string expectedVersion, string initializeResponse, string toolsResponse)
    {
        var initialize = ParseResponse(initializeResponse);
        var tools = ParseResponse(toolsResponse);
        var version = initialize.Element("result")?.Element("serverInfo")?.Element("version")?.Value;
        if (initialize.Element("id")?.Value != "1" || version is null || !VersionsMatch(expectedVersion, version))
            return new("Server starts", ConnectionCheckLevel.Problem, "The server version does not match this add-in; reinstall the matching release.");
        if (tools.Element("id")?.Value != "2" || tools.Element("result")?.Element("tools")?.Elements().Any() != true)
            return new("Server starts", ConnectionCheckLevel.Problem, "The server did not advertise tools; reinstall Model MCP.");
        return new("Server starts", ConnectionCheckLevel.Ok, "The matching server initializes and advertises tools; close this window and ask your AI app to call revit_ping to check Revit access.");
    }
}
