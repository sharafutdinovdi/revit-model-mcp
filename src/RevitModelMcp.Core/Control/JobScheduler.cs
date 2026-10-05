using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Control;

public enum JobState
{
    Queued,
    WaitingRevit,
    Running,
    Done,
    Failed,
    Cancelled
}

public sealed record ScheduledJob(
    string JobId,
    string ClientId,
    string ClientName,
    string Command,
    string Payload,
    DateTimeOffset SubmittedUtc,
    JobState State,
    string? Result,
    int Position);

public sealed record JobSubmission(ScheduledJob? Job, string? Error, int RetryAfterMs);

public sealed record JobCancellation(bool Cancelled, JobState? State, string Message);

public sealed class JobScheduler
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Entry>> _clients = new(StringComparer.Ordinal);
    private readonly Queue<string> _rotation = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _retention;
    private readonly string? _resultDirectory;
    private Entry? _running;

    public JobScheduler(Func<DateTimeOffset>? clock = null, TimeSpan? retention = null, string? resultDirectory = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _retention = retention ?? TimeSpan.FromMinutes(10);
        _resultDirectory = resultDirectory;
    }

    public bool HasPending
    {
        get { lock (_sync) return _rotation.Count > 0; }
    }

    public JobSubmission Submit(string jobId, string clientId, string clientName, string command, string payload)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job id is required.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client id is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.", nameof(command));
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        lock (_sync)
        {
            EvictExpired();
            if (_jobs.ContainsKey(jobId)) return new(null, "duplicate_job_id", 0);
            if (!_clients.TryGetValue(clientId, out var queue))
            {
                queue = new Queue<Entry>();
                _clients.Add(clientId, queue);
            }
            if (queue.Count >= 16) return new(null, "queue_full", 1000);
            var entry = new Entry(jobId, clientId, string.IsNullOrWhiteSpace(clientName) ? "unknown" : clientName,
                command, payload, _clock());
            if (queue.Count == 0) _rotation.Enqueue(clientId);
            queue.Enqueue(entry);
            _jobs.Add(jobId, entry);
            if (ActionJobParser.IsAction(command))
            {
                var accepted = CommandResponse<string>.PartialResult(command, "accepted", "Command accepted and running.", 0);
                accepted.JobId = jobId;
                StoreResult(entry, CommandResponseJsonSerializer.Serialize(accepted));
            }
            return new(Snapshot(entry), null, 0);
        }
    }

    public ScheduledJob? TakeNext()
    {
        lock (_sync)
        {
            EvictExpired();
            if (_running is not null || _rotation.Count == 0) return null;
            var clientId = _rotation.Dequeue();
            var queue = _clients[clientId];
            var entry = queue.Dequeue();
            if (queue.Count > 0) _rotation.Enqueue(clientId);
            else _clients.Remove(clientId);
            entry.State = JobState.Running;
            _running = entry;
            return Snapshot(entry);
        }
    }

    public void MarkWaiting()
    {
        lock (_sync)
        {
            if (_running is not null) return;
            foreach (var queue in _clients.Values)
                foreach (var entry in queue) entry.State = JobState.WaitingRevit;
        }
    }

    public void MarkCancellable(string jobId)
    {
        lock (_sync)
        {
            if (_running?.JobId == jobId) _running.CanCancel = true;
        }
    }

    public void Complete(string jobId, string result, bool success)
    {
        lock (_sync)
        {
            if (_running?.JobId != jobId) throw new InvalidOperationException("The job is not running.");
            _running.State = _running.CancelRequested ? JobState.Cancelled : success ? JobState.Done : JobState.Failed;
            StoreResult(_running, result);
            _running.CompletedUtc = _clock();
            _running = null;
        }
    }

    public JobCancellation Cancel(string jobId, string clientId, bool isAction = false)
    {
        lock (_sync)
        {
            EvictExpired();
            if (!_jobs.TryGetValue(jobId, out var entry) || (entry.ClientId != clientId && !(isAction && ActionJobParser.IsAction(entry.Command))))
                return new(false, null, "Job not found for this client.");
            if (entry.State is JobState.Queued or JobState.WaitingRevit)
            {
                clientId = entry.ClientId;
                var queue = _clients[clientId];
                var remaining = queue.Where(item => item != entry).ToArray();
                _clients.Remove(clientId);
                if (remaining.Length > 0) _clients.Add(clientId, new Queue<Entry>(remaining));
                if (remaining.Length == 0)
                {
                    var rotation = _rotation.Where(item => item != clientId).ToArray();
                    _rotation.Clear();
                    foreach (var item in rotation) _rotation.Enqueue(item);
                }
                entry.State = JobState.Cancelled;
                entry.CompletedUtc = _clock();
                return new(true, entry.State, "Queued job cancelled.");
            }
            if (entry.State == JobState.Running && entry.CanCancel && (!isAction || entry.Command == "process-models"))
            {
                entry.CancelRequested = true;
                return new(true, entry.State, "Cancellation requested; it takes effect between slices or before the next model.");
            }
            return new(false, entry.State, entry.State == JobState.Running
                ? "A running command cannot be interrupted; it will finish."
                : "Job has already finished.");
        }
    }

    public bool IsCancellationRequested(string jobId)
    {
        lock (_sync) return _jobs.TryGetValue(jobId, out var entry) && entry.CancelRequested;
    }

    public void SetCancelledResult(string jobId, string result)
    {
        lock (_sync)
        {
            if (_jobs.TryGetValue(jobId, out var entry) && entry.State == JobState.Cancelled)
                StoreResult(entry, result);
        }
    }

    public ScheduledJob? Status(string jobId)
    {
        lock (_sync)
        {
            EvictExpired();
            return _jobs.TryGetValue(jobId, out var entry) ? Snapshot(entry) : null;
        }
    }

    public IReadOnlyList<ScheduledJob> ActiveJobs()
    {
        lock (_sync)
        {
            EvictExpired();
            return _jobs.Values.Where(entry => entry.State is JobState.Queued or JobState.WaitingRevit or JobState.Running)
                .OrderBy(entry => entry.SubmittedUtc).Select(Snapshot).ToArray();
        }
    }

    private ScheduledJob Snapshot(Entry entry)
    {
        var position = 0;
        if (entry.State is JobState.Queued or JobState.WaitingRevit)
        {
            var queues = _rotation.Select(clientId => _clients[clientId].ToArray()).ToArray();
            for (var index = 0; index < 16; index++)
                foreach (var queue in queues)
                {
                    if (index >= queue.Length) continue;
                    position++;
                    if (ReferenceEquals(queue[index], entry)) goto Found;
                }
        }
    Found:
        return new(entry.JobId, entry.ClientId, entry.ClientName, entry.Command, entry.Payload,
            entry.SubmittedUtc, entry.State, entry.Result, position);
    }

    private void EvictExpired()
    {
        var cutoff = _clock() - _retention;
        foreach (var entry in _jobs.Values.Where(entry => entry.CompletedUtc < (ActionJobParser.IsAction(entry.Command) ? _clock() - TimeSpan.FromHours(24) : cutoff)).ToArray())
        {
            _jobs.Remove(entry.JobId);
            if (_resultDirectory is not null && ActionJobParser.IsAction(entry.Command))
            {
                File.Delete(Path.Combine(_resultDirectory, $"{entry.JobId}.json"));
                File.Delete(Path.Combine(_resultDirectory, $"{entry.JobId}.cancel"));
            }
        }
    }

    public void PublishProgress(string jobId, string response)
    {
        lock (_sync)
        {
            if (_running?.JobId == jobId) StoreResult(_running, response);
        }
    }

    private void StoreResult(Entry entry, string result)
    {
        if (Environment.GetEnvironmentVariable("REVIT_MCP_REDACT_PATHS") == "1")
            result = CommandResponseJsonSerializer.RedactPaths(result);
        entry.Result = result;
        if (_resultDirectory is null || !ActionJobParser.IsAction(entry.Command)) return;
        if (!Guid.TryParseExact(entry.JobId, "N", out _))
            throw new ArgumentException("A persisted job id must be a GUID.");
        CommandResponseJsonFile.WriteContent(Path.Combine(_resultDirectory, $"{entry.JobId}.json"), result);
    }

    private sealed class Entry(string jobId, string clientId, string clientName, string command,
        string payload, DateTimeOffset submittedUtc)
    {
        public string JobId { get; } = jobId;
        public string ClientId { get; } = clientId;
        public string ClientName { get; } = clientName;
        public string Command { get; } = command;
        public string Payload { get; } = payload;
        public DateTimeOffset SubmittedUtc { get; } = submittedUtc;
        public DateTimeOffset? CompletedUtc { get; set; }
        public JobState State { get; set; } = JobState.Queued;
        public string? Result { get; set; }
        public bool CancelRequested { get; set; }
        public bool CanCancel { get; set; }
    }
}
