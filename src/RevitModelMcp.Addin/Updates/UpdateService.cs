using System.ComponentModel;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Updates;

namespace RevitModelMcp.Updates;

internal static class UpdateService
{
    private const string ReleasePath = "/repos/sharafutdinovdi/revit-model-mcp/releases/latest";
    private static readonly string LocalDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp");
    private static readonly string ReleasePage = "https://github.com/sharafutdinovdi/revit-model-mcp/releases";

    public static void Start() => _ = Task.Run(Run);

    private static void Run()
    {
        try
        {
            using var mutex = new Mutex(false, @"Local\RevitModelMcp.UpdateCheck");
            if (!mutex.WaitOne(0)) return;
            try { Check(); }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception exception)
        {
            PluginLog.Warn($"Update check failed. Type='{exception.GetType().Name}'.");
            ActivityRecorder.RecordSystemNotice($"Update check failed: {exception.GetType().Name}.", failed: true);
        }
    }

    private static void Check()
    {
        Directory.CreateDirectory(LocalDirectory);
        var statePath = Path.Combine(LocalDirectory, "update-state.json");
        var state = ReadJson<UpdateState>(statePath) ?? new UpdateState();
        var result = ReadJson<UpdateResult>(Path.Combine(LocalDirectory, "update-result.json"));
        if (result is not null && result.Time > state.ReportedResultTime)
        {
            var message = result.ExitCode == 0 ? $"Updated to {result.Version}" :
                $"Update to {result.Version} failed: {result.Reason ?? FailureReason(result.ExitCode)}";
            ActivityRecorder.RecordSystemNotice(message, ReleaseUrl(result.Version), result.ExitCode != 0);
            state.ReportedResultTime = result.Time;
            state.QueuedVersion = null;
            WriteJson(statePath, state);
        }

        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RevitModelMcp");
        var machine = ReadJson<UpdateSettings>(Path.Combine(programData, "settings.json"));
        var user = ReadJson<UpdateSettings>(Path.Combine(LocalDirectory, "settings.json"));
        if (!UpdatePolicy.IsEnabled(null, machine?.UpdateCheck, user?.UpdateCheck)) return;
        if (state.QueuedVersion is not null)
        {
            if (Mutex.TryOpenExisting(@"Local\RevitModelMcp.Updater", out var runningUpdater))
            {
                using (runningUpdater) return;
            }
            state.QueuedVersion = null;
            state.LastCheck = null;
            WriteJson(statePath, state);
        }
        var now = DateTimeOffset.UtcNow;
        if (!UpdatePolicy.ShouldCheck(now, state.LastCheck)) return;
        state.LastCheck = now;
        WriteJson(statePath, state);

        var feed = FeedBase();
        var release = RequestJson<Release>(new Uri(feed, ReleasePath));
        if (release is null || !UpdatePolicy.IsNewerStable(InstalledVersion(), release.TagName, release.Draft, release.Prerelease)) return;
        if (state.QueuedVersion == release.TagName) return;
        var releaseUrl = ValidDownloadUrl(release.HtmlUrl) ? release.HtmlUrl : ReleaseUrl(release.TagName);
        var assemblyPath = typeof(Application).Assembly.Location;
        var perUserRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var perUser = assemblyPath.StartsWith(perUserRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!perUser)
        {
            if (state.NotifiedVersion == release.TagName) return;
            ActivityRecorder.RecordSystemNotice($"Update available: {release.TagName.TrimStart('v', 'V')}", releaseUrl);
            state.NotifiedVersion = release.TagName;
            WriteJson(statePath, state);
            return;
        }

        var assetName = UpdatePolicy.SelectSingleUserAsset(release.Assets.Select(asset => asset.Name), release.TagName);
        var msiAsset = release.Assets.FirstOrDefault(asset => asset.Name == assetName);
        var checksumAsset = release.Assets.FirstOrDefault(asset => asset.Name == "SHA256SUMS.txt");
        if (msiAsset is null || checksumAsset is null || !ValidDownloadUrl(msiAsset.Url) || !ValidDownloadUrl(checksumAsset.Url))
        {
            ActivityRecorder.RecordSystemNotice($"Update to {release.TagName} failed: release assets are missing or invalid.", releaseUrl, true);
            return;
        }
        var version = release.TagName.TrimStart('v', 'V');
        var directory = Path.Combine(LocalDirectory, "updates", version);
        CreatePrivateDirectory(directory);
        var checksumText = ReadText(new Uri(checksumAsset.Url));
        File.WriteAllText(Path.Combine(directory, "SHA256SUMS.txt"), checksumText);
        var expected = UpdatePolicy.ParseChecksum(checksumText, msiAsset.Name);
        if (expected is null)
        {
            ActivityRecorder.RecordSystemNotice($"Update to {release.TagName} failed: MSI checksum is missing.", releaseUrl, true);
            return;
        }
        var msiPath = Path.Combine(directory, msiAsset.Name);
        try
        {
            Download(new Uri(msiAsset.Url), msiPath);
            using var file = File.OpenRead(msiPath);
            if (!UpdatePolicy.VerifyChecksum(file, expected))
            {
                file.Close();
                File.Delete(msiPath);
                ActivityRecorder.RecordSystemNotice($"Update to {version} failed: SHA256 checksum mismatch.", releaseUrl, true);
                return;
            }
        }
        catch
        {
            if (File.Exists(msiPath)) File.Delete(msiPath);
            throw;
        }
        var updaterPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "RevitModelMcp.Updater.exe");
        if (!File.Exists(updaterPath))
        {
            ActivityRecorder.RecordSystemNotice($"Update to {version} failed: updater is missing.", releaseUrl, true);
            return;
        }
        var stagedUpdaterPath = Path.Combine(directory, "RevitModelMcp.Updater.exe");
        File.Copy(updaterPath, stagedUpdaterPath, true);
        StartUpdater(stagedUpdaterPath,
            $"\"{msiPath}\" {expected} \"{Path.Combine(directory, "install.log")}\"", directory);
        state.QueuedVersion = release.TagName;
        WriteJson(statePath, state);
        ActivityRecorder.RecordSystemNotice($"Update to {version} is ready. It will install after Revit closes.", releaseUrl);
    }

    private static Uri FeedBase()
    {
        var value = Environment.GetEnvironmentVariable("REVIT_MCP_UPDATE_FEED");
        if (string.IsNullOrWhiteSpace(value)) return new Uri("https://api.github.com");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp &&
              (uri.Host == "127.0.0.1" || uri.Host == "localhost")))
            throw new InvalidDataException("REVIT_MCP_UPDATE_FEED must use HTTPS or loopback HTTP.");
        return uri;
    }

    private static bool ValidDownloadUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp &&
         (uri.Host == "127.0.0.1" || uri.Host == "localhost"));

    private static string ReleaseUrl(string version) => $"{ReleasePage}/tag/{version}";

    private static string InstalledVersion() =>
        typeof(Application).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        typeof(Application).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static string FailureReason(int code) => code switch
    {
        13 => "SHA256 checksum mismatch",
        1460 => "Revit was still running after seven days",
        _ => $"installer exit code {code}"
    };

    private static T? RequestJson<T>(Uri uri) where T : class
    {
        using var response = Request(uri).GetResponse();
        using var stream = response.GetResponseStream();
        return stream is null ? null : new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
    }

    private static string ReadText(Uri uri)
    {
        using var response = Request(uri).GetResponse();
        using var stream = response.GetResponseStream()!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Download(Uri uri, string path)
    {
        using var response = Request(uri).GetResponse();
        using var source = response.GetResponseStream()!;
        using var destination = File.Create(path);
        source.CopyTo(destination);
    }

    private static HttpWebRequest Request(Uri uri)
    {
        var request = (HttpWebRequest)WebRequest.Create(uri);
        request.Timeout = 10000;
        request.ReadWriteTimeout = 10000;
        request.UserAgent = "RevitModelMcp updater";
        request.Accept = "application/vnd.github+json, text/plain";
        request.Proxy = WebRequest.DefaultWebProxy;
        if (request.Proxy is not null) request.Proxy.Credentials = CredentialCache.DefaultCredentials;
        return request;
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        return new DataContractJsonSerializer(typeof(T)).ReadObject(file) as T;
    }

    private static void WriteJson<T>(string path, T value)
    {
        using var file = File.Create(path);
        new DataContractJsonSerializer(typeof(T)).WriteObject(file, value);
    }

    private static void CreatePrivateDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        var directory = Directory.CreateDirectory(path);
        directory.SetAccessControl(security);
    }

    private static void StartUpdater(string executablePath, string arguments, string workingDirectory)
    {
        var startupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder($"\"{executablePath}\" {arguments}");
        if (!CreateProcess(executablePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                0x08000000, IntPtr.Zero, workingDirectory, ref startupInfo, out var processInformation))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(processInformation.Thread);
        CloseHandle(processInformation.Process);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

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
    private sealed class Release
    {
        [DataMember(Name = "tag_name")] public string TagName { get; set; } = string.Empty;
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "html_url")] public string HtmlUrl { get; set; } = string.Empty;
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets { get; set; } = [];
    }

    [DataContract]
    private sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
        [DataMember(Name = "browser_download_url")] public string Url { get; set; } = string.Empty;
    }

    [DataContract]
    private sealed class UpdateSettings
    {
        [DataMember(Name = "updateCheck")] public bool? UpdateCheck { get; set; }
    }

    [DataContract]
    private sealed class UpdateState
    {
        [DataMember(Name = "lastCheck")] public DateTimeOffset? LastCheck { get; set; }
        [DataMember(Name = "notifiedVersion")] public string? NotifiedVersion { get; set; }
        [DataMember(Name = "queuedVersion")] public string? QueuedVersion { get; set; }
        [DataMember(Name = "reportedResultTime")] public DateTimeOffset ReportedResultTime { get; set; }
    }

    [DataContract]
    private sealed class UpdateResult
    {
        [DataMember(Name = "version")] public string Version { get; set; } = string.Empty;
        [DataMember(Name = "exitCode")] public int ExitCode { get; set; }
        [DataMember(Name = "time")] public DateTimeOffset Time { get; set; }
        [DataMember(Name = "reason")] public string? Reason { get; set; }
    }
}
