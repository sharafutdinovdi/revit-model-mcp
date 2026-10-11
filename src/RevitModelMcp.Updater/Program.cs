using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace RevitModelMcp.Updater;

internal static class Program
{
    private static int Main(string[] arguments)
    {
        if (arguments.Length != 4) return 2;
        using var mutex = new Mutex(false, @"Local\RevitModelMcp.Updater");
        try
        {
            if (!mutex.WaitOne(0)) return 0;
        }
        catch (AbandonedMutexException)
        {
        }
        try
        {
            var msiPath = Path.GetFullPath(arguments[0]);
            var expected = arguments[1];
            var logPath = Path.GetFullPath(arguments[2]);
            var releaseTag = arguments[3];
            var version = Path.GetFileNameWithoutExtension(msiPath)
                .Replace("RevitModelMcp-", string.Empty).Replace("-SingleUser", string.Empty);
            int exitCode;
            string? reason = null;
            try { exitCode = Run(msiPath, expected, logPath, version, releaseTag); }
            catch (Exception exception)
            {
                exitCode = 1;
                reason = exception.GetType().Name;
            }
            WriteResult(new UpdateResult
            {
                Version = version,
                ReleaseTag = releaseTag,
                ExitCode = exitCode,
                Time = DateTimeOffset.UtcNow,
                Reason = reason
            });
            return exitCode;
        }
        catch (Exception)
        {
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void WriteResult(UpdateResult result)
    {
        var resultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitModelMcp", "update-result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        using var resultFile = File.Create(resultPath);
        new DataContractJsonSerializer(typeof(UpdateResult)).WriteObject(resultFile, result);
    }

    // The bundled server (<root>\Model MCP\server) is in use while an AI app runs it; the MSI cannot replace it then.
    private static bool BundledServerRunning()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Model MCP", "server"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Model MCP", "server")
        }.Select(root => root.TrimEnd('\\') + "\\").ToArray();
        var servers = Process.GetProcessesByName("revit-model-mcp");
        try
        {
            foreach (var process in servers)
            {
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (Win32Exception) { continue; }
                catch (InvalidOperationException) { continue; }
                if (path is not null && roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }
        finally
        {
            foreach (var process in servers) process.Dispose();
        }
    }

    private static int Run(string msiPath, string expected, string logPath, string version, string releaseTag)
    {
        var deadline = DateTimeOffset.UtcNow.AddDays(7);
        var session = Process.GetCurrentProcess().SessionId;
        var noticeWritten = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var revitProcesses = Process.GetProcessesByName("Revit");
            bool revitRunning;
            try
            {
                revitRunning = revitProcesses.Any(process => process.SessionId == session);
            }
            finally
            {
                foreach (var process in revitProcesses) process.Dispose();
            }
            if (!revitRunning)
            {
                if (!BundledServerRunning()) break;
                if (!noticeWritten)
                {
                    noticeWritten = true;
                    WriteResult(new UpdateResult
                    {
                        Version = version,
                        ReleaseTag = releaseTag,
                        Time = DateTimeOffset.UtcNow,
                        Waiting = true,
                        Notice = "Close your AI app to finish updating."
                    });
                }
            }
            Thread.Sleep(TimeSpan.FromSeconds(10));
        }
        if (DateTimeOffset.UtcNow >= deadline) return 1460;
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return 13;
        using var file = new FileStream(msiPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var sha256 = SHA256.Create())
        {
            var actual = BitConverter.ToString(sha256.ComputeHash(file)).Replace("-", string.Empty);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                file.Close();
                File.Delete(msiPath);
                return 13;
            }
        }
        return RunInstaller(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
            $"/i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\"", Path.GetDirectoryName(msiPath)!);
    }

    private static int RunInstaller(string executablePath, string arguments, string workingDirectory)
    {
        var startupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder($"\"{executablePath}\" {arguments}");
        if (!CreateProcess(executablePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                0x08000000, IntPtr.Zero, workingDirectory, ref startupInfo, out var processInformation))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(processInformation.Thread);
        try
        {
            if (WaitForSingleObject(processInformation.Process, 0xFFFFFFFF) != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetExitCodeProcess(processInformation.Process, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)exitCode);
        }
        finally
        {
            CloseHandle(processInformation.Process);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        private IntPtr reserved;
        private IntPtr desktop;
        private IntPtr title;
        private int x;
        private int y;
        private int xSize;
        private int ySize;
        private int xCountChars;
        private int yCountChars;
        private int fillAttribute;
        private int flags;
        private short showWindow;
        private short reserved2;
        private IntPtr reservedPointer;
        private IntPtr standardInput;
        private IntPtr standardOutput;
        private IntPtr standardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        private int processId;
        private int threadId;
    }

    [DataContract]
    private sealed class UpdateResult
    {
        [DataMember(Name = "version")] public string Version { get; set; } = string.Empty;
        [DataMember(Name = "releaseTag")] public string ReleaseTag { get; set; } = string.Empty;
        [DataMember(Name = "exitCode")] public int ExitCode { get; set; }
        [DataMember(Name = "time")] public DateTimeOffset Time { get; set; }
        [DataMember(Name = "reason", EmitDefaultValue = false)] public string? Reason { get; set; }
        [DataMember(Name = "waiting", EmitDefaultValue = false)] public bool Waiting { get; set; }
        [DataMember(Name = "notice", EmitDefaultValue = false)] public string? Notice { get; set; }
    }
}
