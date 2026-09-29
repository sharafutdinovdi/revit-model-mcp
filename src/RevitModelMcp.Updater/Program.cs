using System.Diagnostics;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace RevitModelMcp.Updater;

internal static class Program
{
    private static int Main(string[] arguments)
    {
        if (arguments.Length != 3) return 2;
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
            var version = Path.GetFileNameWithoutExtension(msiPath)
                .Replace("RevitModelMcp-", string.Empty).Replace("-SingleUser", string.Empty);
            int exitCode;
            string? reason = null;
            try { exitCode = Run(msiPath, expected, logPath); }
            catch (Exception exception)
            {
                exitCode = 1;
                reason = exception.GetType().Name;
            }
            var result = new UpdateResult { Version = version, ExitCode = exitCode, Time = DateTimeOffset.UtcNow, Reason = reason };
            var resultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitModelMcp", "update-result.json");
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            using var resultFile = File.Create(resultPath);
            new DataContractJsonSerializer(typeof(UpdateResult)).WriteObject(resultFile, result);
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

    private static int Run(string msiPath, string expected, string logPath)
    {
        var deadline = DateTimeOffset.UtcNow.AddDays(7);
        var session = Process.GetCurrentProcess().SessionId;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var revitProcesses = Process.GetProcessesByName("Revit");
            try
            {
                if (!revitProcesses.Any(process => process.SessionId == session)) break;
            }
            finally
            {
                foreach (var process in revitProcesses) process.Dispose();
            }
            Thread.Sleep(TimeSpan.FromSeconds(10));
        }
        if (DateTimeOffset.UtcNow >= deadline) return 1460;
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return 13;
        using (var file = File.OpenRead(msiPath))
        using (var sha256 = SHA256.Create())
        {
            var actual = BitConverter.ToString(sha256.ComputeHash(file)).Replace("-", string.Empty);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(msiPath);
                return 13;
            }
        }
        using var installer = Process.Start(new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        if (installer is null) return 1;
        installer.WaitForExit();
        return installer.ExitCode;
    }

    [DataContract]
    private sealed class UpdateResult
    {
        [DataMember(Name = "version")] public string Version { get; set; } = string.Empty;
        [DataMember(Name = "exitCode")] public int ExitCode { get; set; }
        [DataMember(Name = "time")] public DateTimeOffset Time { get; set; }
        [DataMember(Name = "reason", EmitDefaultValue = false)] public string? Reason { get; set; }
    }
}
