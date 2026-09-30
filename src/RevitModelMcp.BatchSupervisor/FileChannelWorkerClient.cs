using System.Web.Script.Serialization;

namespace RevitModelMcp.BatchSupervisor;

internal sealed class FileChannelWorkerClient
{
    private readonly JavaScriptSerializer _json = new() { MaxJsonLength = int.MaxValue };

    public static bool MatchesHeartbeat(string path, int processId, string startedUtc, bool requireFresh)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (Convert.ToInt32(json["processId"]) != processId ||
                !DateTimeOffset.TryParse(Convert.ToString(json["startedUtc"]), out var actual) ||
                !DateTimeOffset.TryParse(startedUtc, out var expected) || actual != expected)
                return false;
            return !requireFresh || DateTimeOffset.TryParse(Convert.ToString(json["updatedUtc"]), out var updated) &&
                DateTimeOffset.UtcNow - updated < TimeSpan.FromSeconds(60);
        }
        catch (Exception exception) when (exception is IOException or KeyNotFoundException or FormatException or ArgumentException)
        { return false; }
    }

    public async Task<string> WaitForHeartbeatAsync(int processId, string root, DateTimeOffset deadline,
        Func<bool> cancelled, Func<bool> exited, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, $"instance_{processId}.json");
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cancelled()) throw new OperationCanceledException("Batch run cancelled.");
            if (exited()) throw new InvalidOperationException("Worker exited before its heartbeat.");
            if (File.Exists(path))
            {
                try
                {
                    var status = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    if (Convert.ToInt32(status["processId"]) == processId &&
                        Convert.ToInt32(status["fileChannelVersion"]) == 2 &&
                        DateTimeOffset.TryParse(Convert.ToString(status["updatedUtc"]), out var updated) &&
                        DateTimeOffset.UtcNow - updated < TimeSpan.FromSeconds(60))
                        return Convert.ToString(status["startedUtc"])!;
                }
                catch (Exception exception) when (exception is IOException or KeyNotFoundException or FormatException or ArgumentException) { }
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("Worker startup deadline expired.");
    }

    public async Task<Dictionary<string, object>> SendAsync(int processId, string startedUtc, string root,
        string command, Dictionary<string, object> payload, DateTimeOffset deadline,
        Func<bool> cancelled, Func<bool> exited, CancellationToken cancellationToken)
    {
        var heartbeat = Path.Combine(root, $"instance_{processId}.json");
        if (!MatchesHeartbeat(heartbeat, processId, startedUtc, true))
            throw new InvalidOperationException("Worker heartbeat is stale or identity changed.");
        var directory = Path.Combine(root, "instances", processId.ToString());
        var correlation = Guid.NewGuid().ToString("N");
        var jobId = Guid.NewGuid().ToString("N");
        payload["command"] = command;
        payload["targetProcessId"] = processId;
        payload["correlationId"] = correlation;
        payload["jobId"] = jobId;
        payload["clientId"] = "batch-supervisor";
        payload["clientName"] = "Batch supervisor";
        var temporary = Path.Combine(directory, $"job_{jobId}.tmp");
        var target = Path.Combine(directory, $"job_{jobId}.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(temporary, _json.Serialize(payload));
        if (!MatchesHeartbeat(heartbeat, processId, startedUtc, true))
        {
            File.Delete(temporary);
            throw new InvalidOperationException("Worker identity changed before publication.");
        }
        File.Move(temporary, target);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cancelled()) throw new OperationCanceledException("Batch run cancelled.");
            if (exited()) throw new InvalidOperationException("Worker exited during the job.");
            if (!MatchesHeartbeat(heartbeat, processId, startedUtc, true))
                throw new InvalidOperationException("Worker heartbeat is stale or identity changed.");
            foreach (var responsePath in Directory.EnumerateFiles(directory, $"response_*_{command}_{correlation}.json"))
            {
                var response = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(responsePath));
                if (Convert.ToString(Value(response, "correlationId")) != correlation) continue;
                if (Value(response, "responder") is not Dictionary<string, object> responder ||
                    Convert.ToInt32(Value(responder, "processId")) != processId)
                    throw new InvalidOperationException("Unconfirmed worker response identity.");
                if (Value(response, "success") is not bool success || !success)
                    throw new InvalidOperationException(Convert.ToString(Value(response, "error")) ?? "Worker job failed.");
                return response;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"{command} deadline expired.");
    }

    public string SerializeData(Dictionary<string, object> response) =>
        _json.Serialize(response.TryGetValue("data", out var data) ? data :
            throw new InvalidDataException("Snapshot response has no data."));

    private static object? Value(Dictionary<string, object> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;
}
