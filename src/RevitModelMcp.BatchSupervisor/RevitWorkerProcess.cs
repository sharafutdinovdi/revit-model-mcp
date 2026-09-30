using System.Diagnostics;

namespace RevitModelMcp.BatchSupervisor;

internal sealed class RevitWorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly DateTime _startedUtc;
    private readonly string _heartbeatPath;
    private string? _heartbeatStartedUtc;

    private RevitWorkerProcess(Process process, string channelRoot)
    {
        _process = process;
        _startedUtc = process.StartTime.ToUniversalTime();
        _heartbeatPath = Path.Combine(channelRoot, $"instance_{process.Id}.json");
    }

    public int ProcessId => _process.Id;
    public string ProcessStartedUtc => _startedUtc.ToString("O");
    public bool HasExited => _process.HasExited;
    public string? HeartbeatStartedUtc => _heartbeatStartedUtc;

    public static RevitWorkerProcess Start(string executable, string channelRoot)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Configured Revit executable is absent.");
        var start = new ProcessStartInfo(executable, "/language ENU") { UseShellExecute = false };
        start.EnvironmentVariables["REVIT_MCP_BATCH_WORKER"] = "1";
        start.EnvironmentVariables["REVIT_MCP_CHANNEL_DIR"] = channelRoot;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Revit did not start.");
        return new RevitWorkerProcess(process, channelRoot);
    }

    public void SetHeartbeatIdentity(string startedUtc)
    {
        if (!DateTimeOffset.TryParse(startedUtc, out var heartbeatStart) ||
            heartbeatStart.UtcDateTime < _startedUtc.AddSeconds(-10))
            throw new InvalidOperationException("Worker heartbeat predates the owned process.");
        _heartbeatStartedUtc = startedUtc;
    }

    public void Recycle()
    {
        if (_process.HasExited) return;
        using var current = Process.GetProcessById(_process.Id);
        if (current.StartTime.ToUniversalTime() != _startedUtc)
            throw new InvalidOperationException("The worker PID was replaced; termination refused.");
        if (_heartbeatStartedUtc is not null &&
            !FileChannelWorkerClient.MatchesHeartbeat(_heartbeatPath, _process.Id, _heartbeatStartedUtc, false))
            throw new InvalidOperationException("The worker heartbeat changed; termination refused.");
        current.Kill();
    }

    public static void RecycleInterrupted(int processId, string processStartedUtc,
        string heartbeatStartedUtc, string channelRoot)
    {
        var heartbeatPath = Path.Combine(channelRoot, $"instance_{processId}.json");
        if (!DateTimeOffset.TryParse(processStartedUtc, out var expectedStart) ||
            !FileChannelWorkerClient.MatchesHeartbeat(heartbeatPath, processId, heartbeatStartedUtc, false))
            return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime() != expectedStart.UtcDateTime)
                return;
            process.Kill();
        }
        catch (ArgumentException) { }
    }

    public void Dispose() => _process.Dispose();
}
