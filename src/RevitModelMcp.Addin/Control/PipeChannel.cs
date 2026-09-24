using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Control;

/// <summary>
/// Serves pipe/1 on \\.\pipe\RevitModelMcp.&lt;pid&gt; for the current Windows user. Every job goes through the
/// shared scheduler; this class never touches the Revit API.
/// </summary>
internal sealed class PipeChannel : IDisposable
{
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromMilliseconds(250);
    private readonly ControlChannel _channel;
    private readonly Action _requestExecution;
    private readonly string _revitVersion;
    private readonly Func<IReadOnlyList<InstanceDocument>> _documents;
    private readonly ConcurrentDictionary<string, Task<string>> _completions = new();
    private readonly CancellationTokenSource _shutdown = new();
    private volatile NamedPipeServerStream? _listening;
    private readonly int _processId = Process.GetCurrentProcess().Id;

    public PipeChannel(ControlChannel channel, Action requestExecution, string revitVersion, string instanceId,
        Func<IReadOnlyList<InstanceDocument>> documents)
    {
        _channel = channel;
        _requestExecution = requestExecution;
        _revitVersion = revitVersion;
        _documents = documents;
        InstanceId = instanceId;
        PipeName = PipeProtocol.PipeName(_processId);
    }

    public string PipeName { get; }
    public string InstanceId { get; }

    /// <summary>Creates the first pipe instance, so the pipe accepts clients when this method returns.</summary>
    public void Start()
    {
        var server = CreateServer();
        _ = Task.Run(() => AcceptAsync(server));
        PluginLog.Info($"Pipe listener started. Name='{PipeName}'.");
    }

    private NamedPipeServerStream CreateServer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
#if NETFRAMEWORK
        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
#else
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
#endif
    }

    private async Task AcceptAsync(NamedPipeServerStream? server)
    {
        while (server is not null)
        {
            NamedPipeServerStream? connected = server;
            server = null;
            _listening = connected;
            try
            {
                // .NET Framework may ignore the token here; Dispose also closes the listening instance.
                await connected.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (_shutdown.IsCancellationRequested ||
                                              exception is OperationCanceledException or ObjectDisposedException)
            {
                connected.Dispose();
                return;
            }
            catch (IOException)
            {
                // The client disappeared before the connection completed.
                connected.Dispose();
                connected = null;
            }
            try
            {
                if (!_shutdown.IsCancellationRequested) server = CreateServer();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                PluginLog.Error("Pipe listener stopped: the next pipe instance could not be created.", exception);
            }
            if (connected is not null) _ = Task.Run(() => HandleAsync(connected));
        }
        _listening = null;
    }

    private async Task HandleAsync(NamedPipeServerStream stream)
    {
        var connection = new Connection(stream);
        try
        {
            if (IsRemoteClient(stream))
            {
                PluginLog.Warn("Pipe client from another computer rejected.");
                return;
            }
            var reader = new PipeLineReader(stream);
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (PipeProtocolException exception)
                {
                    // An unframed stream cannot be resynchronized; report and drop the connection.
                    await connection.SendAsync(Error(null, "message_rejected", exception.Message)).ConfigureAwait(false);
                    return;
                }
                if (line is null) return;
                if (line.Length == 0) continue;
                PipeMessage request;
                try
                {
                    request = PipeProtocol.Parse(line);
                }
                catch (PipeProtocolException exception)
                {
                    await connection.SendAsync(Error(null, "invalid_message", exception.Message)).ConfigureAwait(false);
                    continue;
                }
                await DispatchAsync(connection, request).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client disconnected or Revit is shutting down.
        }
        catch (Exception exception)
        {
            PluginLog.Warn($"Pipe connection failed. Type='{exception.GetType().Name}'.");
        }
        finally
        {
            connection.Closed.Cancel();
            CancelQueuedJobs(connection);
            stream.Dispose();
        }
    }

    private async Task DispatchAsync(Connection connection, PipeMessage request)
    {
        PipeMessage reply;
        try
        {
            reply = request.Type switch
            {
                "hello" => Hello(connection, request),
                _ when connection.ClientId is null => Error(request.Id, "hello_required", "Send hello before other messages."),
                "submit" => Submit(connection, request),
                "status" => Status(connection, request),
                "cancel" => Cancel(connection, request),
                _ => Error(request.Id, "unknown_type", $"Unknown message type '{request.Type}'.")
            };
        }
        catch (Exception exception) when (exception is not IOException and not ObjectDisposedException)
        {
            // Job payloads may contain model data; log the type only.
            PluginLog.Warn($"Pipe request failed. Type='{exception.GetType().Name}'.");
            reply = Error(request.Id, "internal_error", "The pipe request failed; check the add-in log.");
        }
        await connection.SendAsync(reply).ConfigureAwait(false);
        // Pushes start after the reply, so a client always sees "submitted" before any job update.
        if (connection.TakeWatchRequest() is { } watch) _ = WatchAsync(connection, watch.JobId, watch.Completion);
        if (reply.Type == "submitted") _requestExecution();
    }

    private PipeMessage Hello(Connection connection, PipeMessage request)
    {
        if (request.Protocol != PipeProtocol.Version)
            return Error(request.Id, "unsupported_protocol", $"This add-in speaks {PipeProtocol.Version}.");
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return Error(request.Id, "client_id_required", "hello requires clientId.");
        if (connection.ClientId is not null && connection.ClientId != request.ClientId)
            return Error(request.Id, "client_id_changed", "A connection keeps the clientId of its first hello.");
        connection.ClientId = request.ClientId;
        return new PipeMessage
        {
            Type = "hello",
            Id = request.Id,
            Protocol = PipeProtocol.Version,
            InstanceId = InstanceId,
            Pid = _processId,
            RevitVersion = _revitVersion,
            Documents = _documents().ToList()
        };
    }

    private PipeMessage Submit(Connection connection, PipeMessage request)
    {
        if (request.Job is null) return Error(request.Id, "job_required", "submit requires a job object.");
        var command = ControlJobParser.Parse(request.Job);
        if (command.ClientId != connection.ClientId)
            return command.Kind == ControlJobKind.Invalid
                ? Error(request.Id, "invalid_job", command.Error ?? "Invalid job.")
                : Error(request.Id, "client_mismatch", "The job clientId must match the hello clientId.");
        if (ActionJobParser.IsAction(command.Command) && ActionCommandExecutor.ReadOnlyMode)
            return Error(request.Id, "read_only", "read-only mode");
        var submitted = _channel.SubmitHttp(command, request.Job, out var completion);
        if (submitted.Job is null || completion is null)
        {
            var error = Error(request.Id, submitted.Error ?? "submission_failed", "The job was not queued.");
            error.RetryAfterMs = submitted.RetryAfterMs;
            error.JobId = command.JobId;
            return error;
        }
        var jobId = submitted.Job.JobId;
        _completions[jobId] = completion;
        _ = completion.ContinueWith(_ => _completions.TryRemove(jobId, out Task<string>? _), TaskScheduler.Default);
        connection.Jobs[jobId] = 0;
        connection.RequestWatch(jobId, completion);
        return new PipeMessage
        {
            Type = "submitted",
            Id = request.Id,
            JobId = jobId,
            State = ControlChannel.StateName(submitted.Job.State),
            Position = submitted.Job.Position
        };
    }

    private PipeMessage Status(Connection connection, PipeMessage request)
    {
        var status = request.JobId is null ? null : _channel.Scheduler.Status(request.JobId);
        if (status is null || status.ClientId != connection.ClientId)
        {
            var error = Error(request.Id, "not_found", "Job not found or expired.");
            error.JobId = request.JobId;
            return error;
        }
        // A reconnected client resumes pushes for its unfinished job.
        if (!IsFinished(status.State) && _completions.TryGetValue(status.JobId, out var completion) &&
            connection.Jobs.TryAdd(status.JobId, 0))
            connection.RequestWatch(status.JobId, completion);
        return new PipeMessage
        {
            Type = "status",
            Id = request.Id,
            JobId = status.JobId,
            State = ControlChannel.StateName(status.State),
            Position = status.Position,
            Result = IsFinished(status.State) ? status.Result : null
        };
    }

    private PipeMessage Cancel(Connection connection, PipeMessage request)
    {
        if (request.JobId is null) return Error(request.Id, "job_id_required", "cancel requires jobId.");
        var cancellation = _channel.CancelJob(request.JobId, connection.ClientId!);
        return new PipeMessage
        {
            Type = "cancel",
            Id = request.Id,
            JobId = request.JobId,
            Cancelled = cancellation.Cancelled,
            State = cancellation.State is null ? "unknown" : ControlChannel.StateName(cancellation.State.Value),
            Message = cancellation.Message
        };
    }

    private async Task WatchAsync(Connection connection, string jobId, Task<string> completion)
    {
        try
        {
            string? sent = null;
            while (!connection.Closed.IsCancellationRequested)
            {
                if (completion.IsCompleted)
                {
                    var final = _channel.Scheduler.Status(jobId);
                    connection.Jobs.TryRemove(jobId, out _);
                    await connection.SendAsync(new PipeMessage
                    {
                        Type = "job",
                        JobId = jobId,
                        State = final is not null && IsFinished(final.State) ? ControlChannel.StateName(final.State) : "failed",
                        Result = await completion.ConfigureAwait(false)
                    }).ConfigureAwait(false);
                    return;
                }
                // Terminal states are pushed only with their result, after the completion is set.
                if (_channel.Scheduler.Status(jobId) is { } status && !IsFinished(status.State))
                {
                    var state = ControlChannel.StateName(status.State);
                    if (state != sent)
                    {
                        sent = state;
                        await connection.SendAsync(new PipeMessage
                        {
                            Type = "job",
                            JobId = jobId,
                            State = state,
                            Position = status.Position
                        }).ConfigureAwait(false);
                    }
                }
                await Task.WhenAny(completion, Task.Delay(StatePollInterval, connection.Closed.Token)).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection closed; a reconnecting client can ask for status.
        }
    }

    private void CancelQueuedJobs(Connection connection)
    {
        if (connection.ClientId is null) return;
        foreach (var jobId in connection.Jobs.Keys)
        {
            // A running job, and above all a running action, always finishes.
            if (_channel.Scheduler.Status(jobId)?.State is not (JobState.Queued or JobState.WaitingRevit)) continue;
            try
            {
                _channel.CancelJob(jobId, connection.ClientId);
            }
            catch (Exception exception)
            {
                PluginLog.Warn($"Queued pipe job could not be cancelled on disconnect. Type='{exception.GetType().Name}'.");
            }
        }
    }

    private static bool IsFinished(JobState state) => state is JobState.Done or JobState.Failed or JobState.Cancelled;

    private static PipeMessage Error(string? id, string error, string message) =>
        new() { Type = "error", Id = id, Error = error, Message = message };

    private static bool IsRemoteClient(NamedPipeServerStream stream)
    {
        // Local clients make the call fail; a client connected over SMB reports its computer name.
        var name = new StringBuilder(256);
        return GetNamedPipeClientComputerName(stream.SafePipeHandle, name, (uint)name.Capacity * 2) &&
               !string.Equals(name.ToString(), Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, StringBuilder clientComputerName,
        uint clientComputerNameLength);

    public void Dispose()
    {
        _shutdown.Cancel();
        _listening?.Dispose();
    }

    private sealed class Connection(Stream stream)
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private (string JobId, Task<string> Completion)? _watchRequest;

        public string? ClientId { get; set; }
        public ConcurrentDictionary<string, byte> Jobs { get; } = new();
        public CancellationTokenSource Closed { get; } = new();

        public void RequestWatch(string jobId, Task<string> completion) => _watchRequest = (jobId, completion);

        public (string JobId, Task<string> Completion)? TakeWatchRequest()
        {
            var watch = _watchRequest;
            _watchRequest = null;
            return watch;
        }

        public async Task SendAsync(PipeMessage message)
        {
            var bytes = Encoding.UTF8.GetBytes(PipeProtocol.Serialize(message) + "\n");
            await _write.WaitAsync(Closed.Token).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, Closed.Token).ConfigureAwait(false);
                await stream.FlushAsync(Closed.Token).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }
    }
}
