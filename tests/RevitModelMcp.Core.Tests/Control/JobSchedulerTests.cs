using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class JobSchedulerTests
{
    private static JobSubmission Submit(JobScheduler scheduler, string client, int number) =>
        scheduler.Submit($"{client}-{number}", client, client, "ping", "{}");

    [Test]
    public async Task RoundRobinPreservesClientFifo()
    {
        var scheduler = new JobScheduler();
        foreach (var client in new[] { "a", "b", "c" })
            for (var number = 1; number <= 2; number++) Submit(scheduler, client, number);
        var taken = new List<string>();
        while (scheduler.TakeNext() is { } job)
        {
            taken.Add(job.JobId);
            scheduler.Complete(job.JobId, "{}", true);
        }
        await Assert.That(string.Join(",", taken)).IsEqualTo("a-1,b-1,c-1,a-2,b-2,c-2");
    }

    [Test]
    public async Task SeventeenthQueuedJobIsRejected()
    {
        var scheduler = new JobScheduler();
        for (var number = 1; number <= 16; number++) Submit(scheduler, "a", number);
        var rejected = Submit(scheduler, "a", 17);
        await Assert.That(rejected.Error).IsEqualTo("queue_full");
        await Assert.That(rejected.RetryAfterMs).IsGreaterThan(0);
        await Assert.That(scheduler.ActiveJobs().Count).IsEqualTo(16);
    }

    [Test]
    public async Task PendingJobReportsWaitingForRevit()
    {
        var scheduler = new JobScheduler();
        Submit(scheduler, "a", 1);
        scheduler.MarkWaiting();
        await Assert.That(scheduler.Status("a-1")?.State).IsEqualTo(JobState.WaitingRevit);
        await Assert.That(scheduler.TakeNext()?.State).IsEqualTo(JobState.Running);
    }

    [Test]
    public async Task QueuedJobCanBeCancelledByItsClient()
    {
        var scheduler = new JobScheduler();
        Submit(scheduler, "a", 1);
        Submit(scheduler, "a", 2);
        await Assert.That(scheduler.Cancel("a-1", "b").Cancelled).IsFalse();
        await Assert.That(scheduler.Cancel("a-1", "a").Cancelled).IsTrue();
        await Assert.That(scheduler.Status("a-1")?.State).IsEqualTo(JobState.Cancelled);
        await Assert.That(scheduler.TakeNext()?.JobId).IsEqualTo("a-2");
    }

    [Test]
    public async Task RunningActionCannotBeInterrupted()
    {
        var scheduler = new JobScheduler();
        scheduler.Submit("action", "a", "Alice", "delete", "{}");
        scheduler.TakeNext();
        var cancellation = scheduler.Cancel("action", "a", isAction: true);
        await Assert.That(cancellation.Cancelled).IsFalse();
        await Assert.That(cancellation.State).IsEqualTo(JobState.Running);
        await Assert.That(cancellation.Message).Contains("cannot be interrupted");
    }

    [Test]
    public async Task RunningReadSessionCancelsBetweenSlices()
    {
        var scheduler = new JobScheduler();
        scheduler.Submit("read", "a", "Alice", "view-elements", "{}");
        scheduler.TakeNext();
        scheduler.MarkCancellable("read");
        await Assert.That(scheduler.Cancel("read", "a").Cancelled).IsTrue();
        await Assert.That(scheduler.IsCancellationRequested("read")).IsTrue();
        scheduler.Complete("read", "{}", false);
        await Assert.That(scheduler.Status("read")?.State).IsEqualTo(JobState.Cancelled);
    }

    [Test]
    public async Task CompletedResultExpiresAfterTenMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var scheduler = new JobScheduler(() => now);
        Submit(scheduler, "a", 1);
        var job = scheduler.TakeNext()!;
        scheduler.Complete(job.JobId, "result", true);
        now += TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1);
        await Assert.That(scheduler.Status(job.JobId)).IsNull();
    }

    [Test]
    public async Task ActionResultIsRetainedForTwentyFourHours()
    {
        var now = DateTimeOffset.UtcNow;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var jobId = Guid.NewGuid().ToString("N");
        var scheduler = new JobScheduler(() => now, resultDirectory: directory);
        try
        {
            scheduler.Submit(jobId, "a", "Alice", "export", "{}");
            scheduler.TakeNext();
            scheduler.Complete(jobId, "{\"command\":\"export\",\"success\":true,\"data\":{}}", true);
            now += TimeSpan.FromHours(23);
            await Assert.That(scheduler.Status(jobId)?.State).IsEqualTo(JobState.Done);
            await Assert.That(File.ReadAllText(Path.Combine(directory, $"{jobId}.json"))).Contains("\"command\":\"export\"");
            now += TimeSpan.FromHours(1) + TimeSpan.FromTicks(1);
            await Assert.That(scheduler.Status(jobId)).IsNull();
            await Assert.That(File.Exists(Path.Combine(directory, $"{jobId}.json"))).IsFalse();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task ProcessModelsCancelsAfterClientRestartAndRetainsCompletedModels()
    {
        var scheduler = new JobScheduler();
        scheduler.Submit("process", "old-client", "Alice", "process-models", "{}");
        scheduler.TakeNext();
        scheduler.MarkCancellable("process");
        scheduler.PublishProgress("process", "one model completed");
        await Assert.That(scheduler.Status("process")?.Result).IsEqualTo("one model completed");
        await Assert.That(scheduler.Cancel("process", "new-client").Cancelled).IsFalse();
        await Assert.That(scheduler.IsCancellationRequested("process")).IsFalse();
        await Assert.That(scheduler.Cancel("process", "new-client", true).Cancelled).IsTrue();
        await Assert.That(scheduler.IsCancellationRequested("process")).IsTrue();
        scheduler.Complete("process", "cancelled with one completed model", true);
        await Assert.That(scheduler.Status("process")?.State).IsEqualTo(JobState.Cancelled);
        await Assert.That(scheduler.Status("process")?.Result).IsEqualTo("cancelled with one completed model");
    }

    [Test]
    public async Task QueuedActionCanBeCancelledAfterClientRestart()
    {
        var scheduler = new JobScheduler();
        scheduler.Submit("export", "old-client", "Alice", "export", "{}");
        await Assert.That(scheduler.Cancel("export", "new-client").Cancelled).IsFalse();
        await Assert.That(scheduler.Status("export")?.State).IsEqualTo(JobState.Queued);
        await Assert.That(scheduler.Cancel("export", "new-client", true).Cancelled).IsTrue();
        await Assert.That(scheduler.TakeNext()).IsNull();
    }

    [Test]
    public async Task ConcurrentSubmissionsKeepCountConsistent()
    {
        var scheduler = new JobScheduler();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(number => Task.Run(() =>
            scheduler.Submit(number.ToString(), number.ToString(), "worker", "ping", "{}"))));
        await Assert.That(scheduler.ActiveJobs().Count).IsEqualTo(100);
    }

    [Test]
    public async Task ResponseIncludesClientAndQueueMetadata()
    {
        JobResponseMetadata.Current = new JobResponseMetadata(
            new ClientIdentity { Name = "codex", Id = "client-1" }, "job-1", 42);
        try
        {
            var json = CommandResponseJsonSerializer.Serialize(CommandResponse<string>.Ok("ping", "pong", 1));
            await Assert.That(json).Contains("\"client\":{");
            await Assert.That(json).Contains("\"name\":\"codex\"");
            await Assert.That(json).Contains("\"id\":\"client-1\"");
            await Assert.That(json).Contains("\"jobId\":\"job-1\"");
            await Assert.That(json).Contains("\"queuedMs\":42");
        }
        finally
        {
            JobResponseMetadata.Current = null;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Test]
    public async Task DashedGuidActionJobIsRejectedAndSchedulerKeepsRunning()
    {
        var directory = NewTempDirectory();
        try
        {
            var scheduler = new JobScheduler(resultDirectory: directory);
            var rejected = scheduler.Submit(Guid.NewGuid().ToString(), "a", "Alice", "delete", "{}");
            await Assert.That(rejected.Error).IsEqualTo("invalid_job_id");
            await Assert.That(scheduler.HasPending).IsFalse();
            await Assert.That(scheduler.ActiveJobs().Count).IsEqualTo(0);
            var actionId = NewId();
            await Assert.That(scheduler.Submit("ping-1", "a", "Alice", "ping", "{}").Error).IsNull();
            await Assert.That(scheduler.Submit(actionId, "a", "Alice", "delete", "{}").Error).IsNull();
            await Assert.That(scheduler.TakeNext()?.JobId).IsEqualTo("ping-1");
            scheduler.Complete("ping-1", "{}", true);
            await Assert.That(scheduler.TakeNext()?.JobId).IsEqualTo(actionId);
            scheduler.Complete(actionId, "{}", true);
            await Assert.That(scheduler.Status(actionId)?.State).IsEqualTo(JobState.Done);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task ResultWriteFailureDoesNotWedgeScheduler()
    {
        var directory = NewTempDirectory();
        try
        {
            var fail = false;
            var scheduler = new JobScheduler(resultDirectory: directory, writeResult: (_, _) =>
            {
                if (fail) throw new IOException("disk full");
            });
            var actionId = NewId();
            await Assert.That(scheduler.Submit(actionId, "a", "Alice", "delete", "{}").Error).IsNull();
            scheduler.Submit("ping-1", "a", "Alice", "ping", "{}");
            await Assert.That(scheduler.TakeNext()?.JobId).IsEqualTo(actionId);
            fail = true;
            scheduler.Complete(actionId, "{\"done\":true}", true);
            var status = scheduler.Status(actionId);
            await Assert.That(status?.State).IsEqualTo(JobState.Done);
            await Assert.That(status?.Result).IsEqualTo("{\"done\":true}");
            await Assert.That(scheduler.TakeNext()).IsNotNull();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task AcceptedWriteFailureRejectsSubmissionWithoutEnqueueing()
    {
        var directory = NewTempDirectory();
        try
        {
            var fail = true;
            var scheduler = new JobScheduler(resultDirectory: directory, writeResult: (_, _) =>
            {
                if (fail) throw new IOException("disk full");
            });
            var actionId = NewId();
            var rejected = scheduler.Submit(actionId, "a", "Alice", "delete", "{}");
            await Assert.That(rejected.Error).IsEqualTo("persist_failed");
            await Assert.That(scheduler.HasPending).IsFalse();
            await Assert.That(scheduler.ActiveJobs().Count).IsEqualTo(0);
            fail = false;
            var accepted = scheduler.Submit(actionId, "a", "Alice", "delete", "{}");
            await Assert.That(accepted.Error).IsNull();
            await Assert.That(accepted.Job?.Position).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task ProgressWriteFailureKeepsInMemoryResult()
    {
        var directory = NewTempDirectory();
        try
        {
            var fail = false;
            var scheduler = new JobScheduler(resultDirectory: directory, writeResult: (_, _) =>
            {
                if (fail) throw new IOException("disk full");
            });
            var actionId = NewId();
            scheduler.Submit(actionId, "a", "Alice", "delete", "{}");
            scheduler.TakeNext();
            fail = true;
            scheduler.PublishProgress(actionId, "{\"progress\":1}");
            await Assert.That(scheduler.Status(actionId)?.Result).IsEqualTo("{\"progress\":1}");
            scheduler.Complete(actionId, "{}", true);
            await Assert.That(scheduler.Status(actionId)?.State).IsEqualTo(JobState.Done);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task EvictExpiredDeleteFailureDoesNotThrow()
    {
        var directory = NewTempDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var logged = new List<string>();
            var scheduler = new JobScheduler(clock: () => now, resultDirectory: directory,
                log: logged.Add, writeResult: (_, _) => { });
            var actionId = NewId();
            scheduler.Submit(actionId, "a", "Alice", "delete", "{}");
            scheduler.TakeNext();
            scheduler.Complete(actionId, "{}", true);
            Directory.CreateDirectory(Path.Combine(directory, $"{actionId}.json"));
            now += TimeSpan.FromHours(25);
            await Assert.That(scheduler.Status(actionId)).IsNull();
            await Assert.That(scheduler.ActiveJobs().Count).IsEqualTo(0);
            await Assert.That(logged.Any(line => line.Contains(directory))).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
