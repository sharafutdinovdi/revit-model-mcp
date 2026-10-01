using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.BatchSupervisor;

internal sealed class RevitWorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly DateTime _startedUtc;
    private readonly string _heartbeatPath;
    private string? _heartbeatStartedUtc;
    private readonly EnumWindowsCallback _windowCallback;
    private readonly EnumWindowsCallback _childCallback;
    private readonly StringBuilder _windowText = new(512);
    private readonly StringBuilder _dialogText = new(2048);
    private bool _trustPromptVisible;

    private RevitWorkerProcess(Process process, string channelRoot)
    {
        _process = process;
        _startedUtc = process.StartTime.ToUniversalTime();
        _heartbeatPath = Path.Combine(channelRoot, $"instance_{process.Id}.json");
        _windowCallback = InspectWindow;
        _childCallback = InspectChild;
    }

    public int ProcessId => _process.Id;
    public string ProcessStartedUtc => _startedUtc.ToString("O");
    public bool HasExited => _process.HasExited;
    public string? HeartbeatStartedUtc => _heartbeatStartedUtc;

    public bool TrustPromptVisible()
    {
        if (_process.HasExited) return false;
        _trustPromptVisible = false;
        EnumWindows(_windowCallback, IntPtr.Zero);
        return _trustPromptVisible;
    }

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

    private bool InspectWindow(IntPtr window, IntPtr parameter)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId != (uint)_process.Id || !IsWindowVisible(window)) return true;
        _windowText.Clear();
        GetWindowText(window, _windowText, _windowText.Capacity);
        var title = _windowText.ToString();
        if (BatchStartupTrustPolicy.MatchesPrompt(title, title))
        {
            _trustPromptVisible = true;
            return false;
        }
        _dialogText.Clear();
        _dialogText.Append(title);
        EnumChildWindows(window, _childCallback, IntPtr.Zero);
        _trustPromptVisible = BatchStartupTrustPolicy.MatchesPrompt(title, _dialogText.ToString());
        return !_trustPromptVisible;
    }

    private bool InspectChild(IntPtr window, IntPtr parameter)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId != (uint)_process.Id || !IsWindowVisible(window)) return true;
        _windowText.Clear();
        SendMessageTimeout(window, 0x000D, (IntPtr)_windowText.Capacity, _windowText,
            0x0002, 25, out _);
        if (_windowText.Length == 0) return true;
        _dialogText.Append(' ').Append(_windowText);
        return true;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr textLength,
        StringBuilder text, uint flags, uint timeoutMilliseconds, out IntPtr result);
}
