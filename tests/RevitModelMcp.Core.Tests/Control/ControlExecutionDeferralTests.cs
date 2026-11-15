using System.Diagnostics;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ControlExecutionDeferralTests
{
    [Test]
    public async Task IdlingRunsDeferredJobExactlyOnceAtBoundAsync()
    {
        var now = 0L;
        var scheduler = new JobScheduler();
        var policy = new ControlExecutionDeferral(() => now);
        var jobId = Submit(scheduler, "first", "activate-view");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += 2 * Stopwatch.Frequency - 1;
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now++;
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(jobId);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        scheduler.Complete(jobId, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        await Assert.That(scheduler.Status(jobId)!.State).IsEqualTo(JobState.Done);
    }

    [Test]
    public async Task ExternalEventRunsBeforeBoundAndResetsWindowAsync()
    {
        var now = 0L;
        var scheduler = new JobScheduler();
        var policy = new ControlExecutionDeferral(() => now);
        var first = Submit(scheduler, "first", "activate-view");
        var second = Submit(scheduler, "second", "activate-view");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += Stopwatch.Frequency;
        await Assert.That(policy.TakeNext(scheduler, false)!.JobId).IsEqualTo(first);
        scheduler.Complete(first, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += Stopwatch.Frequency;
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += Stopwatch.Frequency;
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(second);
    }

    [Test]
    public async Task SecondDeferredJobGetsFullWindowAfterIdlingExecutionAsync()
    {
        var now = 0L;
        var scheduler = new JobScheduler();
        var policy = new ControlExecutionDeferral(() => now);
        var first = Submit(scheduler, "first", "activate-view");
        var second = Submit(scheduler, "first", "activate-document");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += 2 * Stopwatch.Frequency;
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(first);
        scheduler.Complete(first, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += 2 * Stopwatch.Frequency - 1;
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now++;
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(second);
    }

    [Test]
    public async Task CancellationDuringWindowPreservesNextJobsFullWindowAsync()
    {
        var now = 0L;
        var scheduler = new JobScheduler();
        var policy = new ControlExecutionDeferral(() => now);
        var first = Submit(scheduler, "first", "activate-view");
        var second = Submit(scheduler, "first", "activate-view");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += Stopwatch.Frequency;
        await Assert.That(scheduler.Cancel(first, "first", false).Cancelled).IsTrue();
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += Stopwatch.Frequency;
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += Stopwatch.Frequency;
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(second);
    }

    [Test]
    public async Task ReadsBehindDeferredHeadRunInClientRotationOrderAfterBoundAsync()
    {
        var now = 0L;
        var scheduler = new JobScheduler();
        var policy = new ControlExecutionDeferral(() => now);
        var activation = Submit(scheduler, "first", "activate-view");
        var sameClientRead = Submit(scheduler, "first", "document-info");
        var otherClientRead = Submit(scheduler, "second", "query-elements");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += 2 * Stopwatch.Frequency;
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(activation);
        scheduler.Complete(activation, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(otherClientRead);
        scheduler.Complete(otherClientRead, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(sameClientRead);
    }

    [Test]
    public async Task BackwardCounterRebasesWindowAndRunsAfterTwoForwardSecondsAsync()
    {
        var now = 10 * Stopwatch.Frequency;
        var scheduler = new JobScheduler();
        var policy = new ControlExecutionDeferral(() => now);
        var jobId = Submit(scheduler, "first", "activate-view");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now -= 5 * Stopwatch.Frequency;
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += 2 * Stopwatch.Frequency - 1;
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now++;
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(jobId);
    }

    private static string Submit(JobScheduler scheduler, string clientId, string command)
    {
        var jobId = Guid.NewGuid().ToString("N");
        scheduler.Submit(jobId, clientId, clientId, command, "{}");
        return jobId;
    }
}
