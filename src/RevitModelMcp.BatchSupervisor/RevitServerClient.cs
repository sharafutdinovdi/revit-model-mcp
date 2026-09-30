using System.Net.Http;
using System.Web.Script.Serialization;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.BatchSupervisor;

internal sealed class RevitServerClient
{
    private readonly JavaScriptSerializer _json = new();

    public async Task<(int SavedYear, string ActivitySource)> PrePassAsync(string modelPath,
        CancellationToken cancellationToken)
    {
        var configured = Environment.GetEnvironmentVariable("REVIT_MCP_RSN_REST_BASE");
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") || !baseUri.AbsolutePath.EndsWith(".svc/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RSN REST base is not configured. Set REVIT_MCP_RSN_REST_BASE to the matching AdminRESTService.svc/ URL.");
        if (!modelPath.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An RSN model path is required.");
        var segments = modelPath.Substring(6).Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], baseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RSN host does not match the configured REST endpoint.");
        var modelName = segments[segments.Length - 1];
        var folder = string.Join("/", segments.Skip(1).Take(segments.Length - 2).Select(Uri.EscapeDataString));
        var model = string.Join("/", segments.Skip(1).Select(Uri.EscapeDataString));
        using var handler = new HttpClientHandler { UseDefaultCredentials = true };
        using var client = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Name", Environment.UserName);
        client.DefaultRequestHeaders.Add("User-Machine-Name", Environment.MachineName);
        client.DefaultRequestHeaders.Add("Operation-GUID", Guid.NewGuid().ToString("D"));
        var contents = await GetAsync(client, $"{folder}/contents", cancellationToken);
        if (!contents.TryGetValue("Models", out var modelsValue) || modelsValue is not object[] models)
            throw new InvalidOperationException("RSN /contents response has no Models list.");
        var match = models.OfType<Dictionary<string, object>>().SingleOrDefault(item =>
            string.Equals(Convert.ToString(item.TryGetValue("Name", out var name) ? name : null), modelName, StringComparison.OrdinalIgnoreCase));
        if (match is null || !match.TryGetValue("ProductVersion", out var productVersion))
            throw new InvalidOperationException("RSN /contents has no ProductVersion for the model.");
        var savedYear = BatchYearRouter.Parse(Convert.ToString(productVersion)!);
        await GetAsync(client, $"{model}/modelInfo", cancellationToken);
        await GetAsync(client, $"{model}/history", cancellationToken);
        return (savedYear, "RSN /modelInfo and /history");
    }

    private async Task<Dictionary<string, object>> GetAsync(HttpClient client, string relative,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(relative, cancellationToken);
        response.EnsureSuccessStatusCode();
        return _json.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
    }
}
