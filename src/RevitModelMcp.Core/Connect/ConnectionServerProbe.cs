using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RevitModelMcp.Core.Connect;

public static class ConnectionServerProbe
{
    public static Task<ConnectionCheckResult> CheckAsync(McpServerSpec? spec, string addinVersion, CancellationToken cancellationToken) =>
        Task.Run(() => Check(spec, addinVersion, cancellationToken), CancellationToken.None);

    private static ConnectionCheckResult Check(McpServerSpec? spec, string addinVersion, CancellationToken cancellationToken)
    {
        if (spec is null || spec == McpServerSpec.Uvx || !File.Exists(spec.Command))
            return new("Server starts", ConnectionCheckLevel.Warning, "Install the bundled executable to check startup without downloading packages.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(spec.Command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var pair in McpServerSpec.Env) process.StartInfo.EnvironmentVariables[pair.Key] = pair.Value;
        // The probe uses the resolved bundled executable, never a foreign client command or uvx.
        IntPtr job = IntPtr.Zero;
        try
        {
            if (spec.Args.Count != 0) throw new InvalidOperationException("The bundled server must have no launcher arguments.");
            timeout.Token.ThrowIfCancellationRequested();
            process.Start();
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                job = CreateJobObject(IntPtr.Zero, null);
                var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
                if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobLimits))) ||
                    !AssignProcessToJobObject(job, process.Handle))
                    throw new InvalidOperationException("Cannot contain the diagnostic process.");
            }
            process.ErrorDataReceived += (_, _) => { };
            using var registration = timeout.Token.Register(() =>
            {
                CloseJob(ref job);
                Stop(process);
            });
            process.BeginErrorReadLine();
            process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"model-mcp-connection-check\",\"version\":\"1\"}}}");
            process.StandardInput.Flush();
            var initialize = ReadResponse(process, "1", timeout.Token);
            process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            process.StandardInput.Flush();
            var tools = ReadResponse(process, "2", timeout.Token);
            return ConnectionCheck.ServerStarted(addinVersion, initialize, tools);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Xml.XmlException or OperationCanceledException)
        {
            return new("Server starts", ConnectionCheckLevel.Problem, timeout.IsCancellationRequested
                ? "The startup check was cancelled or exceeded 20 seconds; restart Revit and try again."
                : "The server could not complete MCP startup safely; reinstall Model MCP and try again.");
        }
        finally
        {
            CloseJob(ref job);
            Stop(process);
        }
    }

    private static string ReadResponse(Process process, string id, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = process.StandardOutput.ReadLine() ?? throw new IOException("The server closed its output.");
            if (line.Length > 8 * 1024 * 1024) throw new IOException("The response is too large.");
            if (ConnectionCheck.ParseResponse(line).Element("id")?.Value == id) return line;
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit(1000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private static void CloseJob(ref IntPtr job)
    {
        var handle = Interlocked.Exchange(ref job, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobLimits information, uint length);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
