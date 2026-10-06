using System.Runtime.Serialization.Json;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ProcessModelsResultAssemblerTests
{
    [Test]
    public async Task AppendCancelled_AfterOneProcessed_AppendsRemainingModelsInOrder()
    {
        string[] paths = ["one.rvt", "two.rvt", "three.rvt", "four.rvt"];
        var processed = new ProcessModelResult { Path = paths[0], Status = "done", ElapsedMs = 42 };
        var models = new List<ProcessModelResult> { processed };

        ProcessModelsResultAssembler.AppendCancelled(models, paths, 1);

        await Assert.That(models.Count).IsEqualTo(paths.Length);
        await Assert.That(ReferenceEquals(models[0], processed)).IsTrue();
        await Assert.That(models[0].Status).IsEqualTo("done");
        await Assert.That(models[0].ElapsedMs).IsEqualTo(42L);
        for (var index = 1; index < paths.Length; index++)
        {
            await Assert.That(models[index].Path).IsEqualTo(paths[index]);
            await Assert.That(models[index].Status).IsEqualTo("cancelled");
            await Assert.That(models[index].ElapsedMs).IsEqualTo(0L);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task AppendCancelled_IndexAtOrBeforeStart_AppendsAllModels(int firstUnprocessedIndex)
    {
        string[] paths = ["one.rvt", "two.rvt", "three.rvt", "four.rvt"];
        var models = new List<ProcessModelResult>();

        ProcessModelsResultAssembler.AppendCancelled(models, paths, firstUnprocessedIndex);

        await Assert.That(models.Count).IsEqualTo(paths.Length);
        for (var index = 0; index < paths.Length; index++)
        {
            await Assert.That(models[index].Path).IsEqualTo(paths[index]);
            await Assert.That(models[index].Status).IsEqualTo("cancelled");
            await Assert.That(models[index].ElapsedMs).IsEqualTo(0L);
        }
    }

    [Test]
    [Arguments(4)]
    [Arguments(5)]
    public async Task AppendCancelled_IndexAtOrAfterEnd_AppendsNothing(int firstUnprocessedIndex)
    {
        string[] paths = ["one.rvt", "two.rvt", "three.rvt", "four.rvt"];
        var processed = new ProcessModelResult { Path = paths[0], Status = "done" };
        var models = new List<ProcessModelResult> { processed };

        ProcessModelsResultAssembler.AppendCancelled(models, paths, firstUnprocessedIndex);

        await Assert.That(models.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(models[0], processed)).IsTrue();
    }

    [Test]
    public async Task AppendCancelled_PreservesRefusedEntriesAheadOfModelResults()
    {
        string[] paths = ["one.rvt", "two.rvt"];
        var refused = new ProcessModelResult { Path = "refused.rvt", Status = "skipped", Error = "Refused model." };
        var processed = new ProcessModelResult { Path = paths[0], Status = "done" };
        var models = new List<ProcessModelResult> { refused, processed };

        ProcessModelsResultAssembler.AppendCancelled(models, paths, 1);

        await Assert.That(models.Count).IsEqualTo(paths.Length + 1);
        await Assert.That(ReferenceEquals(models[0], refused)).IsTrue();
        await Assert.That(models[0].Status).IsEqualTo("skipped");
        await Assert.That(models[0].Error).IsEqualTo("Refused model.");
        await Assert.That(ReferenceEquals(models[1], processed)).IsTrue();
        await Assert.That(models[2].Path).IsEqualTo(paths[1]);
        await Assert.That(models[2].Status).IsEqualTo("cancelled");
        await Assert.That(models[2].ElapsedMs).IsEqualTo(0L);
    }

    [Test]
    public async Task AppendCancelled_NullArguments_Throws()
    {
        await Assert.That(() => ProcessModelsResultAssembler.AppendCancelled(null!, [], 0))
            .Throws<ArgumentNullException>();
        await Assert.That(() => ProcessModelsResultAssembler.AppendCancelled([], null!, 0))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CancelledResult_JsonRoundTrip_PreservesStatusPathAndElapsedTime()
    {
        var serializer = new DataContractJsonSerializer(typeof(ProcessModelResult));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, new ProcessModelResult { Path = "one.rvt", Status = "cancelled", ElapsedMs = 0 });
        stream.Position = 0;

        var result = (ProcessModelResult)serializer.ReadObject(stream)!;

        await Assert.That(result.Path).IsEqualTo("one.rvt");
        await Assert.That(result.Status).IsEqualTo("cancelled");
        await Assert.That(result.ElapsedMs).IsEqualTo(0L);
    }
}
