using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal sealed class HttpChannel : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, HttpJob> _jobs = new();
    private readonly ControlChannel _channel;
    private readonly Action _requestExecution;
    private readonly HttpSettings _settings;
    private readonly string _version;
    private readonly int _processId = Process.GetCurrentProcess().Id;
    private readonly CancellationTokenSource _shutdown = new();
    private Timer? _cleanup;
    private volatile string _documentName = string.Empty;

    public HttpChannel(ControlChannel channel, Action requestExecution, string version, HttpSettings settings)
    {
        _channel = channel;
        _requestExecution = requestExecution;
        _version = version;
        _settings = settings;
    }

    public void UpdateDocument(string? name) => _documentName = name ?? string.Empty;

    public void Start()
    {
        if (!_settings.HttpEnabled)
        {
            PluginLog.Info("HTTP listener disabled.");
            return;
        }
        var prefix = $"http://{(_settings.HttpBind == "0.0.0.0" ? "+" : _settings.HttpBind)}:{_settings.HttpPort}/";
        _listener.Prefixes.Add(prefix);
        try
        {
            _listener.Start();
            _cleanup = new Timer(_ => RemoveExpiredResults(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            _ = Task.Run(ListenAsync);
            PluginLog.Info($"HTTP listener started. Prefix='{prefix}'.");
        }
        catch (HttpListenerException exception) when (exception.NativeErrorCode == 5)
        {
            using var identity = WindowsIdentity.GetCurrent();
            PluginLog.Warn($"HTTP listener NOT started: access denied for '{prefix}'. Run once from an elevated command prompt: netsh http add urlacl url={prefix} user=\"{identity.Name}\"");
        }
        catch (HttpListenerException exception)
        {
            PluginLog.Error($"HTTP listener failed at {prefix}. Check the port and other Revit instances.", exception);
        }
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(context));
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                if (!_shutdown.IsCancellationRequested) PluginLog.Error("HTTP listener stopped unexpectedly.", exception);
                return;
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            // HTTP.sys prefixes match Host headers; the local address also constrains the interface.
            if (_settings.HttpBind != "0.0.0.0" &&
                !Equals(context.Request.LocalEndPoint.Address, IPAddress.Parse(_settings.HttpBind)))
            {
                await JsonAsync(context, 403, new() { ["error"] = "This network interface is disabled." }).ConfigureAwait(false);
                return;
            }
            var path = context.Request.Url!.AbsolutePath;
            var method = context.Request.HttpMethod;
            if (method == "GET" && path == "/health")
            {
                await JsonAsync(context, 200, new()
                {
                    ["ok"] = true,
                    ["revitVersion"] = _version,
                    ["documentName"] = _documentName,
                    ["processId"] = _processId,
                    ["readOnly"] = !ActionCommandExecutor.ActionsEnabled
                }).ConfigureAwait(false);
                return;
            }
            if (!Authenticated(context.Request.Headers["Authorization"]))
            {
                context.Response.AddHeader("WWW-Authenticate", "Bearer");
                await JsonAsync(context, 401, new() { ["error"] = "A valid bearer token is required." }).ConfigureAwait(false);
                return;
            }
            if (method == "GET" && path.StartsWith("/jobs/", StringComparison.Ordinal))
            {
                RemoveExpiredResults();
                if (!_jobs.TryGetValue(path.Substring(6), out var existing))
                {
                    await JsonAsync(context, 404, new() { ["error"] = "Job not found or expired." }).ConfigureAwait(false);
                    return;
                }
                await SendJobAsync(context, existing, 0).ConfigureAwait(false);
                return;
            }
            if (method == "POST" && path == "/jobs")
            {
                if (!TryTimeout(context, out var timeout)) return;
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await context.Request.InputStream.ReadAsync(buffer, 0, buffer.Length, _shutdown.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (body.Length + read > 1024 * 1024)
                    {
                        await JsonAsync(context, 413, new() { ["error"] = "Job exceeds 1 MiB." }).ConfigureAwait(false);
                        return;
                    }
                    body.Write(buffer, 0, read);
                }
                var job = await SubmitAsync(context, ControlJobParser.Parse(Encoding.UTF8.GetString(body.ToArray()))).ConfigureAwait(false);
                if (job is not null) await SendJobAsync(context, job, timeout).ConfigureAwait(false);
                return;
            }
            if (method == "GET" && path.StartsWith("/views/", StringComparison.Ordinal) && path.EndsWith("/image", StringComparison.Ordinal))
            {
                var pixel = 1600;
                var parameter = context.Request.QueryString["pixel"];
                if (parameter is not null && (!int.TryParse(parameter, out pixel) || pixel is < 1 or > 4000))
                {
                    await JsonAsync(context, 400, new() { ["error"] = "pixel must be between 1 and 4000." }).ConfigureAwait(false);
                    return;
                }
                var name = Uri.UnescapeDataString(path.Substring(7, path.Length - 13));
                var payload = new Dictionary<string, object>
                {
                    ["command"] = "export-view",
                    ["view"] = name,
                    ["pixelSize"] = pixel,
                    ["zoomToFit"] = true
                };
                if (context.Request.QueryString["document"] is { } document) payload["targetDocument"] = document;
                HttpJob? job;
                if (context.Request.QueryString["jobId"] is { } jobId)
                {
                    RemoveExpiredResults();
                    if (!_jobs.TryGetValue(jobId, out job) || job.Command.Kind != ControlJobKind.ExportView ||
                        job.Command.View != name || job.Command.PixelSize != pixel ||
                        job.Command.TargetDocument != context.Request.QueryString["document"])
                    {
                        await JsonAsync(context, 404, new() { ["error"] = "Matching image job not found or expired." }).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    job = await SubmitAsync(context, ControlJobParser.Parse(HttpSettings.Serialize(payload))).ConfigureAwait(false);
                }
                if (job is null) return;
                if (!await WaitAsync(job, 120).ConfigureAwait(false))
                {
                    await JsonAsync(context, 202, new() { ["jobId"] = job.Id, ["correlationId"] = job.Command.CorrelationId ?? string.Empty }).ConfigureAwait(false);
                    return;
                }
                var result = await job.Completion.ConfigureAwait(false);
                using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(result), System.Xml.XmlDictionaryReaderQuotas.Max);
                var response = XElement.Load(reader);
                if (response.Element("success")?.Value != "true")
                {
                    await BytesAsync(context, 422, Encoding.UTF8.GetBytes(result), "application/json").ConfigureAwait(false);
                    return;
                }
                var fileName = response.Element("data")?.Element("fileName")?.Value;
                if (string.IsNullOrEmpty(fileName) || Path.GetFileName(fileName) != fileName)
                    throw new InvalidDataException("Invalid image artifact name.");
                var imagePath = Path.Combine(SnapshotFileWriter.OutputDirectory, fileName);
                var bytes = File.ReadAllBytes(imagePath);
                job.ImagePath = imagePath;
                context.Response.Headers["X-Revit-Job-Id"] = job.Id;
                await BytesAsync(context, 200, bytes, "image/png").ConfigureAwait(false);
                return;
            }
            await JsonAsync(context, 404, new() { ["error"] = "Endpoint not found." }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Request headers, bodies and exception messages may contain credentials.
            PluginLog.Warn($"HTTP request failed. Type='{exception.GetType().Name}'.");
            try { await JsonAsync(context, 500, new() { ["error"] = "HTTP request failed; check the add-in log." }).ConfigureAwait(false); }
            catch (Exception responseException)
            {
                PluginLog.Warn($"HTTP error response could not be delivered. Type='{responseException.GetType().Name}'.");
                context.Response.Abort();
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private bool Authenticated(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        using var hash = SHA256.Create();
        var actual = hash.ComputeHash(Encoding.UTF8.GetBytes(authorization.Substring(7)));
        var expected = hash.ComputeHash(Encoding.UTF8.GetBytes(_settings.Token));
        var difference = 0;
        for (var index = 0; index < expected.Length; index++) difference |= actual[index] ^ expected[index];
        return difference == 0;
    }

    private static bool TryTimeout(HttpListenerContext context, out int timeout)
    {
        timeout = 120;
        var value = context.Request.QueryString["timeout"];
        if (value is null) return true;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) && timeout is >= 0 and <= 600) return true;
        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes("{\"error\":\"timeout must be between 0 and 600 seconds.\"}");
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        return false;
    }

    private async Task<HttpJob?> SubmitAsync(HttpListenerContext context, ControlJobParseResult command)
    {
        if (ActionJobParser.IsAction(command.Command) && !ActionCommandExecutor.ActionsEnabled)
        {
            await JsonAsync(context, 403, new() { ["error"] = "actions disabled on the workstation", ["correlationId"] = command.CorrelationId ?? string.Empty }).ConfigureAwait(false);
            return null;
        }
        if (!_channel.TrySubmit(command, out var completion))
        {
            await JsonAsync(context, 409, new() { ["error"] = "The add-in is busy with another command.", ["correlationId"] = command.CorrelationId ?? string.Empty }).ConfigureAwait(false);
            return null;
        }
        var job = new HttpJob(Guid.NewGuid().ToString("N"), command, completion!);
        _jobs[job.Id] = job;
        _ = MarkCompletedAsync(job);
        _requestExecution();
        return job;
    }

    private static async Task MarkCompletedAsync(HttpJob job)
    {
        await job.Completion.ConfigureAwait(false);
        Interlocked.Exchange(ref job.CompletedTicks, DateTime.UtcNow.Ticks);
    }

    private async Task<bool> WaitAsync(HttpJob job, int seconds)
    {
        if (job.Completion.IsCompleted) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var delay = Task.Delay(TimeSpan.FromSeconds(seconds), timeout.Token);
        var completed = await Task.WhenAny(job.Completion, delay).ConfigureAwait(false) == job.Completion;
        timeout.Cancel();
        return completed;
    }

    private async Task SendJobAsync(HttpListenerContext context, HttpJob job, int seconds)
    {
        context.Response.Headers["X-Revit-Job-Id"] = job.Id;
        if (await WaitAsync(job, seconds).ConfigureAwait(false))
            await BytesAsync(context, 200, Encoding.UTF8.GetBytes(await job.Completion.ConfigureAwait(false)), "application/json").ConfigureAwait(false);
        else
            await JsonAsync(context, 202, new() { ["jobId"] = job.Id, ["correlationId"] = job.Command.CorrelationId ?? string.Empty }).ConfigureAwait(false);
    }

    private void RemoveExpiredResults()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10).Ticks;
        foreach (var entry in _jobs)
        {
            var completed = Interlocked.Read(ref entry.Value.CompletedTicks);
            if (completed == 0 || completed >= cutoff) continue;
            if (_jobs.TryRemove(entry.Key, out var removed) && removed.ImagePath is not null)
            {
                try { File.Delete(removed.ImagePath); }
                catch (IOException) { PluginLog.Warn("Expired HTTP image could not be deleted."); }
                catch (UnauthorizedAccessException) { PluginLog.Warn("Expired HTTP image could not be deleted."); }
            }
        }
    }

    private static Task JsonAsync(HttpListenerContext context, int status, Dictionary<string, object> payload) =>
        BytesAsync(context, status, Encoding.UTF8.GetBytes(HttpSettings.Serialize(payload)), "application/json");

    private static async Task BytesAsync(HttpListenerContext context, int status, byte[] bytes, string contentType)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _cleanup?.Dispose();
        _listener.Close();
    }

    private sealed class HttpJob(string id, ControlJobParseResult command, Task<string> completion)
    {
        public string Id { get; } = id;
        public ControlJobParseResult Command { get; } = command;
        public Task<string> Completion { get; } = completion;
        public long CompletedTicks;
        public string? ImagePath;
    }
}

[DataContract]
internal sealed record HttpSettings
{
    [DataMember(Name = "httpEnabled", Order = 1)] public bool HttpEnabled { get; set; } = true;
    [DataMember(Name = "httpBind", Order = 2)] public string HttpBind { get; set; } = "127.0.0.1";
    [DataMember(Name = "httpPort", Order = 3)] public int HttpPort { get; set; } = 53110;
    [DataMember(Name = "token", Order = 4)] public string Token { get; set; } = string.Empty;

    [OnDeserializing]
    private void SetDefaults(StreamingContext context)
    {
        HttpEnabled = true;
        HttpBind = "127.0.0.1";
        HttpPort = 53110;
        Token = string.Empty;
    }

    public static HttpSettings Load(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var serializer = new DataContractJsonSerializer(typeof(HttpSettings));
        HttpSettings settings;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        var rights = FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.ChangePermissions;
#if NETFRAMEWORK
        using (var stream = new FileStream(path, FileMode.OpenOrCreate, rights, FileShare.None, 4096, FileOptions.None, security))
#else
        using (var stream = new FileInfo(path).Create(FileMode.OpenOrCreate, rights, FileShare.None, 4096, FileOptions.None, security))
#endif
        {
            stream.SetAccessControl(security);
            settings = stream.Length == 0 ? new HttpSettings() : (HttpSettings)serializer.ReadObject(stream)!;
            if (string.IsNullOrWhiteSpace(settings.Token))
            {
                var bytes = new byte[32];
                using var random = RandomNumberGenerator.Create();
                random.GetBytes(bytes);
                settings.Token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                stream.Position = 0;
                serializer.WriteObject(stream, settings);
                stream.SetLength(stream.Position);
            }
        }
        if (Environment.GetEnvironmentVariable("REVIT_MCP_HTTP_ENABLED") is { } enabled)
            settings.HttpEnabled = enabled switch { "0" => false, "1" => true, _ => throw new InvalidDataException("REVIT_MCP_HTTP_ENABLED must be 0 or 1.") };
        settings.HttpBind = Environment.GetEnvironmentVariable("REVIT_MCP_HTTP_BIND") ?? settings.HttpBind;
        if (Environment.GetEnvironmentVariable("REVIT_MCP_HTTP_PORT") is { } port)
            settings.HttpPort = int.TryParse(port, out var parsed) ? parsed : 0;
        settings.Token = Environment.GetEnvironmentVariable("REVIT_MCP_TOKEN") ?? settings.Token;
        if (!IPAddress.TryParse(settings.HttpBind, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("httpBind must be an IPv4 interface address, or explicitly 0.0.0.0.");
        if (settings.HttpPort is < 1 or > 65535) throw new InvalidDataException("httpPort must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(settings.Token) || settings.Token.Any(char.IsControl))
            throw new InvalidDataException("The HTTP token must be nonempty and contain no control characters.");
        return settings;
    }

    internal static string Serialize(Dictionary<string, object> value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(Dictionary<string, object>),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
