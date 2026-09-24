namespace RevitModelMcp.Core.Models;

/// <summary>
/// One newline-delimited JSON message of the pipe/1 protocol. Unused fields stay null and are not written.
/// </summary>
public sealed class PipeMessage
{
    public string Type { get; set; } = string.Empty;
    public string? Id { get; set; }
    public string? Protocol { get; set; }
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? InstanceId { get; set; }
    public int? Pid { get; set; }
    public string? RevitVersion { get; set; }
    public List<InstanceDocument>? Documents { get; set; }
    public string? JobId { get; set; }
    public string? State { get; set; }
    public int? Position { get; set; }
    public bool? Cancelled { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public int? RetryAfterMs { get; set; }

    /// <summary>Raw JSON object of a submitted job.</summary>
    public string? Job { get; set; }

    /// <summary>Raw JSON object of a finished job's command response.</summary>
    public string? Result { get; set; }
}
