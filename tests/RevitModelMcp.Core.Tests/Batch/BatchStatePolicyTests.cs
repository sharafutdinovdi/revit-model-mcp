using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchStatePolicyTests
{
    private static BatchRun Run(params BatchModelStatus[] statuses) => new()
    {
        RunId = "run",
        Models = statuses.Select((status, index) => new BatchModel { Path = $"{index}.rvt", Status = status }).ToArray()
    };

    [Test]
    public async Task Transition_ProtectsTerminalAndIllegalStates()
    {
        var run = Run(BatchModelStatus.Pending);
        await Assert.That(() => BatchStatePolicy.Transition(run, 0, BatchModelStatus.Completed)).Throws<InvalidOperationException>();
        var running = BatchStatePolicy.Transition(run, 0, BatchModelStatus.Running);
        var completed = BatchStatePolicy.Transition(running, 0, BatchModelStatus.Completed);
        await Assert.That(completed.Status).IsEqualTo(BatchRunStatus.Completed);
        await Assert.That(() => BatchStatePolicy.Transition(completed, 0, BatchModelStatus.Running)).Throws<InvalidOperationException>();
    }


    [Test]
    public async Task Transition_EnforcesRunningOutcomesAndKeepsOtherTerminalModels()
    {
        var run = Run(BatchModelStatus.Completed, BatchModelStatus.Pending, BatchModelStatus.Failed);
        var running = BatchStatePolicy.Transition(run, 1, BatchModelStatus.Running);
        await Assert.That(running.Status).IsEqualTo(BatchRunStatus.Running);
        var failed = BatchStatePolicy.Transition(running, 1, BatchModelStatus.Failed);
        await Assert.That(failed.Status).IsEqualTo(BatchRunStatus.Completed);
        await Assert.That(failed.Models[0].Status).IsEqualTo(BatchModelStatus.Completed);
        await Assert.That(failed.Models[2].Status).IsEqualTo(BatchModelStatus.Failed);
        await Assert.That(() => BatchStatePolicy.Transition(failed, 1, BatchModelStatus.Running))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Resume_SkipsTerminalAndReturnsInterruptedModelToPending()
    {
        var resumed = BatchStatePolicy.Resume(Run(BatchModelStatus.Completed, BatchModelStatus.Failed, BatchModelStatus.Running));
        await Assert.That(resumed.Models.Select(model => model.Status).ToArray())
            .IsEquivalentTo(new[] { BatchModelStatus.Completed, BatchModelStatus.Failed, BatchModelStatus.Pending });
    }

    [Test]
    public async Task Cancel_IsDurableAndPreventsNewWork()
    {
        var cancelled = BatchStatePolicy.Cancel(Run(BatchModelStatus.Pending, BatchModelStatus.Running));
        await Assert.That(cancelled.CancelRequested).IsTrue();
        await Assert.That(cancelled.Models[0].Status).IsEqualTo(BatchModelStatus.Cancelled);
        await Assert.That(() => BatchStatePolicy.Transition(cancelled, 1, BatchModelStatus.Completed)).Throws<InvalidOperationException>();
    }
}
