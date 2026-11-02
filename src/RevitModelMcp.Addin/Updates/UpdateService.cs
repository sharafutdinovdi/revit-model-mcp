using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Updates;
using RevitModelMcp.Policy;

namespace RevitModelMcp.Updates;

internal static class UpdateService
{
    private static class JsonSerializer<T>
    {
        internal static readonly DataContractJsonSerializer Instance = new(typeof(T));
    }

    private static readonly HttpClient Client = CreateClient();

    private const string ReleasePath = "/repos/sharafutdinovdi/revit-model-mcp/releases/latest";
    private static readonly string LocalDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp");
    private static readonly string ReleasePage = "https://github.com/sharafutdinovdi/revit-model-mcp/releases";

    public static void Start() => _ = Task.Run(() => Run(false));

    public static void CheckNow() => _ = Task.Run(() => Run(true));

    private static void Run(bool manual)
    {
        string? checkResult = null;
        try
        {
            using var mutex = new Mutex(false, @"Local\RevitModelMcp.UpdateCheck");
            if (!mutex.WaitOne(0))
            {
                if (manual) ActivityRecorder.RecordSystemNotice("An update check is already running.");
                return;
            }
            try { Check(manual, ref checkResult); }
            finally { mutex.ReleaseMutex(); }
        }
        catch (UpdateDownloadRefusedException exception)
        {
            checkResult = "failed (download refused)";
            PluginLog.Warn(exception.Message);
            ActivityRecorder.RecordSystemNotice(exception.Message, failed: true);
        }
        catch (Exception exception)
        {
            checkResult = $"failed ({exception.GetType().Name})";
            PluginLog.Warn($"Update check failed. Type='{exception.GetType().Name}'.");
            ActivityRecorder.RecordSystemNotice($"Update check failed: {exception.GetType().Name}.", failed: true);
        }
        finally
        {
            if (checkResult is not null) PluginLog.Info($"Update check completed: {checkResult}.");
        }
    }

    private static void Check(bool manual, ref string? checkResult)
    {
        Directory.CreateDirectory(LocalDirectory);
        var statePath = Path.Combine(LocalDirectory, "update-state.json");
        var state = ReadJson<UpdateState>(statePath) ?? new UpdateState();
        var result = ReadJson<UpdateResult>(Path.Combine(LocalDirectory, "update-result.json"));
        if (result is not null && result.Time > state.ReportedResultTime)
        {
            var resultReleaseUrl = UpdatePolicy.BuildReleaseUrl(ReleasePage, result.ReleaseTag, result.Version);
            var outcome = UpdatePolicy.ClassifyInstallerExit(result.ExitCode);
            if (result.Waiting)
            {
                ActivityRecorder.RecordSystemNotice(result.Notice ?? "Close your AI app to finish updating.", resultReleaseUrl);
                state.ReportedResultTime = result.Time;
                WriteJson(statePath, state);
            }
            else
            {
                var message = outcome switch
                {
                    InstallerOutcome.Succeeded => $"Updated to {result.Version}",
                    InstallerOutcome.SucceededRestartRequired => $"Updated to {result.Version}. Restart the computer to finish the update.",
                    _ => $"Update to {result.Version} failed: {result.Reason ?? FailureReason(result.ExitCode)}"
                };
                ActivityRecorder.RecordSystemNotice(message, resultReleaseUrl, outcome == InstallerOutcome.Failed);
                state.ReportedResultTime = result.Time;
                state.QueuedVersion = null;
                if (outcome == InstallerOutcome.Failed)
                {
                    var failed = result.Version.TrimStart('v', 'V');
                    state.BlockedVersions.RemoveAll(item => item.Version == failed);
                    state.BlockedVersions.Add(new BlockedVersion { Version = failed, Until = DateTimeOffset.UtcNow + UpdatePolicy.BlockDuration });
                }
                WriteJson(statePath, state);
            }
        }

        var assemblyPath = typeof(Application).Assembly.Location;
        var perUserRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var perUser = assemblyPath.StartsWith(perUserRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RevitModelMcp");
        var machine = ReadJson<UpdateSettings>(Path.Combine(programData, "settings.json"));
        var user = ReadJson<UpdateSettings>(Path.Combine(LocalDirectory, "settings.json"));
        var mode = UpdateDecision.Decide(new UpdateInputs(Channel(), perUser, PolicyProvider.Current.UpdateMode,
            machine?.UpdateMode, user?.UpdateMode, machine?.UpdateCheck, user?.UpdateCheck,
            Environment.GetEnvironmentVariable("REVIT_MCP_NO_UPDATE_CHECK")));
        if (mode == UpdateMode.Off)
        {
            if (manual) ActivityRecorder.RecordSystemNotice("Update checks are disabled by the configured update policy.");
            return;
        }
        if (state.QueuedVersion is not null)
        {
            if (Mutex.TryOpenExisting(@"Local\RevitModelMcp.Updater", out var runningUpdater))
            {
                using (runningUpdater)
                {
                    if (manual) ActivityRecorder.RecordSystemNotice("An update is already queued. Close Revit to finish installing it.");
                    return;
                }
            }
            state.QueuedVersion = null;
            state.LastCheck = null;
            WriteJson(statePath, state);
        }
        var now = DateTimeOffset.UtcNow;
        if (!manual && !UpdatePolicy.ShouldCheck(now, state.LastCheck)) return;
        state.LastCheck = now;
        WriteJson(statePath, state);

        var feed = FeedBase();
        var release = RequestJsonAsync<Release>(new Uri(feed, ReleasePath)).GetAwaiter().GetResult();
        if (release is null) throw new InvalidDataException("The release feed returned no release.");
        if (!UpdatePolicy.IsNewerStable(InstalledVersion(), release.TagName, release.Draft, release.Prerelease))
        {
            checkResult = "up to date";
            if (manual) ActivityRecorder.RecordSystemNotice("No newer stable Model MCP release is available.");
            return;
        }
        checkResult = $"update available {release.TagName.TrimStart('v', 'V')}";
        if (state.QueuedVersion == release.TagName) return;
        var blocked = state.BlockedVersions.ToDictionary(item => item.Version, item => item.Until);
        if (UpdatePolicy.IsBlocked(release.TagName, blocked, now))
        {
            if (manual) ActivityRecorder.RecordSystemNotice("This update is temporarily blocked after a failed installation or checksum check.", failed: true);
            return;
        }
        var releaseUrl = UpdatePolicy.IsAllowedDownloadUrl(release.HtmlUrl, feed) ? release.HtmlUrl :
            UpdatePolicy.BuildReleaseUrl(ReleasePage, release.TagName, release.TagName);
        if (mode == UpdateMode.Notify)
        {
            if (!manual && state.NotifiedVersion == release.TagName) return;
            var hint = perUser ? " To install updates automatically, set \"updateMode\": \"auto\" in %LOCALAPPDATA%\\RevitModelMcp\\settings.json." : string.Empty;
            ActivityRecorder.RecordSystemNotice($"Update available: {release.TagName.TrimStart('v', 'V')}.{hint}", releaseUrl);
            state.NotifiedVersion = release.TagName;
            WriteJson(statePath, state);
            return;
        }

        var assetName = UpdatePolicy.SelectSingleUserAsset(release.Assets.Select(asset => asset.Name), release.TagName);
        var msiAsset = release.Assets.FirstOrDefault(asset => asset.Name == assetName);
        var checksumAsset = release.Assets.FirstOrDefault(asset => asset.Name == "SHA256SUMS.txt");
        if (msiAsset is null || checksumAsset is null)
        {
            ActivityRecorder.RecordSystemNotice($"Update to {release.TagName} failed: release assets are missing.", releaseUrl, true);
            return;
        }
        UpdatePolicy.EnsureAllowedDownloadUrl(msiAsset.Url, feed);
        UpdatePolicy.EnsureAllowedDownloadUrl(checksumAsset.Url, feed);

        var version = release.TagName.TrimStart('v', 'V');
        var directory = Path.Combine(LocalDirectory, "updates", version);
        CreatePrivateDirectory(directory);
        var checksumText = ReadTextAsync(new Uri(checksumAsset.Url), feed).GetAwaiter().GetResult();
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
            DownloadAsync(new Uri(msiAsset.Url), msiPath, feed).GetAwaiter().GetResult();
            using var file = File.OpenRead(msiPath);
            if (!UpdatePolicy.VerifyChecksum(file, expected))
            {
                file.Close();
                File.Delete(msiPath);
                state.BlockedVersions.RemoveAll(item => item.Version == version);
                state.BlockedVersions.Add(new BlockedVersion { Version = version, Until = now + UpdatePolicy.BlockDuration });
                WriteJson(statePath, state);
                ActivityRecorder.RecordSystemNotice($"Update to {version} failed: SHA256 checksum mismatch. This version is skipped for seven days.", releaseUrl, true);
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
            $"\"{msiPath}\" {expected} \"{Path.Combine(directory, "install.log")}\" \"{release.TagName}\"", directory);
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

    internal static UpdateChannel Channel()
    {
        string? Metadata(string key) => typeof(Application).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
        return UpdateDecision.ResolveChannel(Metadata("UpdateChannel"),
            string.Equals(Metadata("ManagedInstall"), "true", StringComparison.OrdinalIgnoreCase), InstalledVersion());
    }

    private static string InstalledVersion() =>
        typeof(Application).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        typeof(Application).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static string FailureReason(int code) => code switch
    {
        13 => "SHA256 checksum mismatch",
        1460 => "Revit or the bundled server was still running after seven days",
        _ => $"installer exit code {code}"
    };

    private static async Task<T?> RequestJsonAsync<T>(Uri uri) where T : class
    {
        using var response = await RequestAsync(uri).ConfigureAwait(false);
        using var stream = new MemoryStream();
        await CopyResponseAsync(response, stream).ConfigureAwait(false);
        stream.Position = 0;
        return JsonSerializer<T>.Instance.ReadObject(stream) as T;
    }

    private static async Task<string> ReadTextAsync(Uri uri, Uri feed)
    {
        using var response = await AllowedResponseAsync(uri, feed).ConfigureAwait(false);
        using var stream = new MemoryStream();
        await CopyResponseAsync(response, stream).ConfigureAwait(false);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task DownloadAsync(Uri uri, string path, Uri feed)
    {
        using var response = await AllowedResponseAsync(uri, feed).ConfigureAwait(false);
        using var destination = File.Create(path);
        await CopyResponseAsync(response, destination).ConfigureAwait(false);
    }

    private static async Task CopyResponseAsync(HttpResponseMessage response, Stream destination)
    {
        using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        // Disposing the response also interrupts reads on .NET Framework streams.
        using var registration = cancellation.Token.Register(response.Dispose);
        var buffer = new byte[81920];
        while (true)
        {
            cancellation.CancelAfter(10000);
            var count = await source.ReadAsync(buffer, 0, buffer.Length, cancellation.Token).ConfigureAwait(false);
            cancellation.CancelAfter(Timeout.Infinite);
            if (count == 0) return;
            await destination.WriteAsync(buffer, 0, count).ConfigureAwait(false);
        }
    }

    // Redirects are followed by the framework. The final host is checked before any body is read.
    private static async Task<HttpResponseMessage> AllowedResponseAsync(Uri uri, Uri feed)
    {
        var response = await RequestAsync(uri).ConfigureAwait(false);
        try
        {
            UpdatePolicy.EnsureAllowedDownloadUrl(response.RequestMessage!.RequestUri!.AbsoluteUri, feed);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> RequestAsync(Uri uri)
    {
        var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        var proxy = WebRequest.DefaultWebProxy;
        if (proxy is not null) proxy.Credentials = CredentialCache.DefaultCredentials;
        var client = new HttpClient(new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = proxy is not null,
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 50
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RevitModelMcp updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, text/plain");
        return client;
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        return JsonSerializer<T>.Instance.ReadObject(file) as T;
    }

    private static void WriteJson<T>(string path, T value)
    {
        using var file = File.Create(path);
        JsonSerializer<T>.Instance.WriteObject(file, value);
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
        [DataMember(Name = "updateMode")] public string? UpdateMode { get; set; }
    }

    [DataContract]
    private sealed class BlockedVersion
    {
        [DataMember(Name = "version")] public string Version { get; set; } = string.Empty;
        [DataMember(Name = "until")] public DateTimeOffset Until { get; set; }
    }

    [DataContract]
    private sealed class UpdateState
    {
        [DataMember(Name = "lastCheck")] public DateTimeOffset? LastCheck { get; set; }
        [DataMember(Name = "notifiedVersion")] public string? NotifiedVersion { get; set; }
        [DataMember(Name = "blockedVersions", EmitDefaultValue = false)] private List<BlockedVersion>? blockedVersions;
        public List<BlockedVersion> BlockedVersions => blockedVersions ??= [];
        [DataMember(Name = "queuedVersion")] public string? QueuedVersion { get; set; }
        [DataMember(Name = "reportedResultTime")] public DateTimeOffset ReportedResultTime { get; set; }
    }

    [DataContract]
    private sealed class UpdateResult
    {
        [DataMember(Name = "version")] public string Version { get; set; } = string.Empty;
        [DataMember(Name = "releaseTag", EmitDefaultValue = false)] public string? ReleaseTag { get; set; }
        [DataMember(Name = "exitCode")] public int ExitCode { get; set; }
        [DataMember(Name = "time")] public DateTimeOffset Time { get; set; }
        [DataMember(Name = "reason")] public string? Reason { get; set; }
        [DataMember(Name = "waiting", EmitDefaultValue = false)] public bool Waiting { get; set; }
        [DataMember(Name = "notice", EmitDefaultValue = false)] public string? Notice { get; set; }
    }
}
