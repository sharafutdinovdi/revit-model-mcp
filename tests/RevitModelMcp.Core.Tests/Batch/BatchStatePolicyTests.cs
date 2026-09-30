using System.Runtime.Serialization.Json;
using System.Text;
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

    [Test]
    public async Task PythonShapedRun_NormalizesCollectionsForResumeTransitionAndTiming()
    {
        const string json = """{"runId":"run","status":0,"models":[{"path":"A.rvt","status":0}]}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var serializer = new DataContractJsonSerializer(typeof(BatchRun));
        var run = (BatchRun)serializer.ReadObject(stream)!;
        await Assert.That(run.Years.Length).IsEqualTo(0);
        await Assert.That(run.ParameterRules.Length).IsEqualTo(0);
        await Assert.That(run.Models[0].PhaseTimingsMs.Count).IsEqualTo(0);
        var resumed = BatchStatePolicy.Resume(run);
        var running = BatchStatePolicy.Transition(resumed, 0, BatchModelStatus.Running);
        var timed = BatchStatePolicy.Timed(running.Models[0], BatchPhase.Open, 42);
        await Assert.That(timed.PhaseTimingsMs["Open"]).IsEqualTo(42);

        const string nullCollections = """{"runId":"run","years":null,"parameterRules":null,"models":[{"path":"B.rvt","phaseTimingsMs":null}]}""";
        using var nullStream = new MemoryStream(Encoding.UTF8.GetBytes(nullCollections));
        var normalized = (BatchRun)serializer.ReadObject(nullStream)!;
        await Assert.That(normalized.Years.Length).IsEqualTo(0);
        await Assert.That(normalized.ParameterRules.Length).IsEqualTo(0);
        await Assert.That(normalized.Models[0].PhaseTimingsMs.Count).IsEqualTo(0);

        using var emptyStream = new MemoryStream(Encoding.UTF8.GetBytes("""{"runId":"run","models":null}"""));
        var emptyRun = (BatchRun)serializer.ReadObject(emptyStream)!;
        await Assert.That(BatchStatePolicy.Resume(emptyRun).Models.Length).IsEqualTo(0);
    }

    [Test]
    public async Task FailSupervisor_TerminalizesNonterminalModelsAndPreservesResults()
    {
        var run = Run(BatchModelStatus.Completed, BatchModelStatus.Pending, BatchModelStatus.Running);
        var failed = BatchStatePolicy.FailSupervisor(run, "Batch supervisor failed.");
        await Assert.That(failed.Status).IsEqualTo(BatchRunStatus.Failed);
        await Assert.That(failed.Models[0].Status).IsEqualTo(BatchModelStatus.Completed);
        await Assert.That(failed.Models[1].Status).IsEqualTo(BatchModelStatus.Failed);
        await Assert.That(failed.Models[2].Status).IsEqualTo(BatchModelStatus.Failed);
        await Assert.That(failed.Models[1].Error).IsEqualTo("Batch supervisor failed.");
        await Assert.That(BatchStatePolicy.Resume(failed).Status).IsEqualTo(BatchRunStatus.Failed);
    }
}
