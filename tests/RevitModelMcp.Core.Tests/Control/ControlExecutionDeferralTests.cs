using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ControlExecutionDeferralTests
{
    [Test]
    public async Task IdlingRunsDeferredJobExactlyOnceAtBoundAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var scheduler = new JobScheduler(() => now);
        var policy = new ControlExecutionDeferral(() => now);
        var jobId = Submit(scheduler, "first", "activate-view");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromMilliseconds(1999);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromMilliseconds(1);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(jobId);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        scheduler.Complete(jobId, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        await Assert.That(scheduler.Status(jobId)!.State).IsEqualTo(JobState.Done);
    }

    [Test]
    public async Task ExternalEventRunsBeforeBoundAndResetsWindowAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var scheduler = new JobScheduler(() => now);
        var policy = new ControlExecutionDeferral(() => now);
        var first = Submit(scheduler, "first", "activate-view");
        var second = Submit(scheduler, "second", "activate-view");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromSeconds(1);
        await Assert.That(policy.TakeNext(scheduler, false)!.JobId).IsEqualTo(first);
        scheduler.Complete(first, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromSeconds(1);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromSeconds(1);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(second);
    }

    [Test]
    public async Task SecondDeferredJobGetsFullWindowAfterIdlingExecutionAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var scheduler = new JobScheduler(() => now);
        var policy = new ControlExecutionDeferral(() => now);
        var first = Submit(scheduler, "first", "activate-view");
        var second = Submit(scheduler, "first", "activate-document");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromSeconds(2);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(first);
        scheduler.Complete(first, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromMilliseconds(1999);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromMilliseconds(1);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(second);
    }

    [Test]
    public async Task CancellationDuringWindowPreservesNextJobsFullWindowAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var scheduler = new JobScheduler(() => now);
        var policy = new ControlExecutionDeferral(() => now);
        var first = Submit(scheduler, "first", "activate-view");
        var second = Submit(scheduler, "first", "activate-view");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromSeconds(1);
        await Assert.That(scheduler.Cancel(first, "first", false).Cancelled).IsTrue();
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromSeconds(1);
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromSeconds(1);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(second);
    }

    [Test]
    public async Task ReadsBehindDeferredHeadRunInClientRotationOrderAfterBoundAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var scheduler = new JobScheduler(() => now);
        var policy = new ControlExecutionDeferral(() => now);
        var activation = Submit(scheduler, "first", "activate-view");
        var sameClientRead = Submit(scheduler, "first", "document-info");
        var otherClientRead = Submit(scheduler, "second", "query-elements");
        await Assert.That(policy.TakeNext(scheduler, true)).IsNull();
        now += TimeSpan.FromSeconds(2);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(activation);
        scheduler.Complete(activation, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(otherClientRead);
        scheduler.Complete(otherClientRead, "{}", true);
        await Assert.That(policy.TakeNext(scheduler, true)!.JobId).IsEqualTo(sameClientRead);
    }

    private static string Submit(JobScheduler scheduler, string clientId, string command)
    {
        var jobId = Guid.NewGuid().ToString("N");
        scheduler.Submit(jobId, clientId, clientId, command, "{}");
        return jobId;
    }
}
