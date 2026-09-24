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
}
