namespace RevitModelMcp.Core.Control;

public sealed class HttpListenerStatus
{
    public const string Disabled = "disabled", Listening = "listening", Failed = "failed";

    private HttpListenerStatus(string state, string? reason)
    {
        State = state;
        Reason = reason;
    }

    public string State { get; }
    public string? Reason { get; }
    public bool IsFailed => State == Failed;

    public static HttpListenerStatus DisabledBySettings() => new(Disabled, "HTTP is disabled in settings.");

    public static HttpListenerStatus Started() => new(Listening, null);

    public static HttpListenerStatus ConfigError(string exceptionTypeName) => new(Failed,
        $"HTTP settings could not be loaded or are invalid ({exceptionTypeName}). Check settings.json and the REVIT_MCP_HTTP_* variables.");

    public static HttpListenerStatus FromStartFailure(int nativeErrorCode, string prefix) => new(Failed,
        nativeErrorCode switch
        {
            5 => $"Access denied for {prefix}. The URL reservation is missing: check netsh http show urlacl and add one with netsh http add urlacl.",
            32 or 183 => $"The port is already in use for {prefix}. Another Revit instance or program holds it; check netsh http show servicestate.",
            _ => $"The listener could not start for {prefix} (error {nativeErrorCode}). Check netsh http show urlacl and netsh http show servicestate."
        });

    public static HttpListenerStatus FromSelfProbe(int? statusCode, string? exceptionTypeName) => statusCode switch
    {
        200 => Started(),
        503 => new(Failed, "HTTP.sys answered 503 to a self-probe although the listener started. Another service may own this URL or the URL reservation is wrong: check netsh http show urlacl and netsh http show servicestate."),
        null => new(Failed, $"The self-probe did not complete ({exceptionTypeName})."),
        _ => new(Failed, $"The self-probe returned HTTP {statusCode}.")
    };
}
