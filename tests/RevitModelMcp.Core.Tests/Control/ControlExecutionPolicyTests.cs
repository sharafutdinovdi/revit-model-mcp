using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ControlExecutionPolicyTests
{
    [Test]
    [Arguments("activate-view")]
    [Arguments("activate-document")]
    [Arguments("open-document")]
    [Arguments("new-document")]
    [Arguments("close-document")]
    [Arguments("close-views")]
    [Arguments("show")]
    [Arguments("execute-code")]
    [Arguments("run-tool")]
    [Arguments("batch")]
    [Arguments("process-models")]
    public async Task UiSwitchingCommandsRequireExternalEventAsync(string command) =>
        await Assert.That(ControlExecutionPolicy.CanExecuteFromIdling(command)).IsFalse();

    [Test]
    [Arguments("document-info")]
    [Arguments("query-elements")]
    [Arguments("view-elements")]
    [Arguments("jobs")]
    [Arguments("move")]
    public async Task OtherCommandsRetainIdlingFallbackAsync(string command) =>
        await Assert.That(ControlExecutionPolicy.CanExecuteFromIdling(command)).IsTrue();

    [Test]
    public async Task DeferredHeadPreservesStateAndClientRotationAsync()
    {
        var scheduler = new JobScheduler();
        var activationId = Guid.NewGuid().ToString("N");
        var readId = Guid.NewGuid().ToString("N");
        scheduler.Submit(activationId, "first", "First", "activate-view", "{}");
        scheduler.Submit(readId, "second", "Second", "document-info", "{}");
        scheduler.MarkWaiting();

        await Assert.That(scheduler.TakeNext((_, command) => ControlExecutionPolicy.CanExecuteFromIdling(command))).IsNull();
        await Assert.That(scheduler.Status(activationId)!.State).IsEqualTo(JobState.WaitingRevit);
        await Assert.That(scheduler.Status(readId)!.State).IsEqualTo(JobState.WaitingRevit);
        await Assert.That(scheduler.TakeNext()!.JobId).IsEqualTo(activationId);
        await Assert.That(scheduler.TakeNext()).IsNull();
        scheduler.Complete(activationId, "{}", true);
        await Assert.That(scheduler.TakeNext((_, command) => ControlExecutionPolicy.CanExecuteFromIdling(command))!.JobId).IsEqualTo(readId);
    }

    [Test]
    public async Task IdlingDeferralRaisesExternalEventAndExecutesOnlyOnceAsync()
    {
        var scheduler = new JobScheduler();
        var jobId = Guid.NewGuid().ToString("N");
        scheduler.Submit(jobId, "client", "Client", "activate-view", "{}");
        var raises = 0;
        var executions = 0;
        var requests = new ExternalEventRequestQueue(() => { raises++; });
        requests.Request();
        requests.Execute(() =>
        {
            if (scheduler.TakeNext((_, command) => ControlExecutionPolicy.CanExecuteFromIdling(command)) is not null) executions++;
            if (scheduler.HasPending) requests.Request();
        }, exception => throw new InvalidOperationException("Unexpected dispatch failure.", exception));
        await Assert.That(raises).IsEqualTo(2);
        await Assert.That(requests.HasPendingRequest).IsTrue();

        void ExecuteActivation()
        {
            var job = scheduler.TakeNext();
            if (job is null) return;
            executions++;
            scheduler.Complete(job.JobId, "{}", true);
        }
        requests.Execute(ExecuteActivation, exception => throw new InvalidOperationException("Unexpected dispatch failure.", exception));
        requests.Execute(ExecuteActivation, exception => throw new InvalidOperationException("Unexpected dispatch failure.", exception));
        await Assert.That(executions).IsEqualTo(1);
        await Assert.That(scheduler.Status(jobId)!.State).IsEqualTo(JobState.Done);
        await Assert.That(requests.HasPendingRequest).IsFalse();
    }

    [Test]
    public async Task DeferredJobCanStillBeCancelledAsync()
    {
        var scheduler = new JobScheduler();
        var jobId = Guid.NewGuid().ToString("N");
        scheduler.Submit(jobId, "client", "Client", "activate-view", "{}");
        await Assert.That(scheduler.TakeNext((_, command) => ControlExecutionPolicy.CanExecuteFromIdling(command))).IsNull();
        await Assert.That(scheduler.Cancel(jobId, "client", false).Cancelled).IsTrue();
        await Assert.That(scheduler.TakeNext()).IsNull();
    }
}
