using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Batch;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchDialogPolicyTests
{
    [Test]
    public async Task Decide_DeniesUnknownIdOrRuntimeType()
    {
        await Assert.That(BatchDialogPolicy.Decide("unknown", "TaskDialogShowingEventArgs").Recycle).IsTrue();
        await Assert.That(BatchDialogPolicy.Decide("TaskDialog_Missing_Third_Party_Updater", "DialogBoxShowingEventArgs").Recycle).IsTrue();
    }

    [Test]
    public async Task Decide_OverridesOnlyConfiguredPair()
    {
        var choices = new Dictionary<(string Id, string Type), int>
        {
            [("known", "TaskDialogShowingEventArgs")] = 1002
        };
        await Assert.That(BatchDialogPolicy.Decide("known", "TaskDialogShowingEventArgs", choices).Allowed).IsTrue();
        await Assert.That(BatchDialogPolicy.Decide("known", "DialogBoxShowingEventArgs", choices).Recycle).IsTrue();
    }

    [Test]
    public async Task Load_MissingFileKeepsBuiltInChoicesEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
        await Assert.That(BatchDialogPolicy.Load(path)).IsEmpty();
    }

    [Test]
    public async Task Load_UnreadablePathFailsWithFileName()
    {
        var path = WriteAllowlist("[]");
        try
        {
            using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var exception = Assert.Throws<FormatException>(() => BatchDialogPolicy.Load(path));
                await Assert.That(exception.Message).Contains(path);
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Load_ValidFileMergesWithoutReplacingBuiltInPairs()
    {
        var path = WriteAllowlist("""[{"dialogId":"known","type":"TaskDialogShowingEventArgs","result":1}]""");
        try
        {
            var choices = BatchDialogPolicy.Load(path, new Dictionary<(string Id, string Type), int>
            {
                [("built-in", "TaskDialogShowingEventArgs")] = 1002
            });
            await Assert.That(BatchDialogPolicy.Decide("known", "TaskDialogShowingEventArgs", choices).OverrideResult).IsEqualTo(1);
            await Assert.That(BatchDialogPolicy.Decide("built-in", "TaskDialogShowingEventArgs", choices).OverrideResult).IsEqualTo(1002);
            await Assert.That(BatchDialogPolicy.Decide("known", "DialogBoxShowingEventArgs", choices).Allowed).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Test]
    [Arguments("{")]
    [Arguments("{}")]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("[null]")]
    [Arguments("[[]]")]
    [Arguments("[{}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\"t\"}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\"t\",\"result\":1,\"extra\":2}]")]
    [Arguments("[{\"dialogId\":1,\"type\":\"t\",\"result\":1}]")]
    [Arguments("[{\"dialogId\":\" \" ,\"type\":\"t\",\"result\":1}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\" \" ,\"result\":1}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\"t\",\"result\":1.5}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\"t\",\"result\":\"1\"}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\"t\",\"result\":true}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\"t\",\"result\":null}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\"t\",\"result\":2147483648}]")]
    [Arguments("[{\"dialogId\":\"x\",\"dialogId\":\"x\",\"type\":\"t\",\"result\":1}]")]
    [Arguments("[{\"dialogId\":\"x\",\"type\":\"t\",\"result\":1},{\"dialogId\":\"x\",\"type\":\"t\",\"result\":2}]")]
    public async Task Load_RejectsInvalidShapes(string json)
    {
        var path = WriteAllowlist(json);
        try
        {
            var exception = Assert.Throws<FormatException>(() => BatchDialogPolicy.Load(path));
            await Assert.That(exception.Message).Contains(path);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Load_RejectsDuplicateBuiltInPair()
    {
        var path = WriteAllowlist("""[{"dialogId":"known","type":"TaskDialogShowingEventArgs","result":1}]""");
        try
        {
            await Assert.That(() => BatchDialogPolicy.Load(path, new Dictionary<(string Id, string Type), int>
            {
                [("known", "TaskDialogShowingEventArgs")] = 1002
            })).Throws<FormatException>();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Append_PreservesFirstTwentyRecordsAndOldModelDeserializes()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{"path":"C:\\A.rvt","status":0}"""));
        var model = (BatchModel)new DataContractJsonSerializer(typeof(BatchModel)).ReadObject(stream)!;
        var records = Enumerable.Range(0, 25).Select(index => new BatchDialogRecord
        {
            DialogId = index.ToString(),
            Type = "TaskDialogShowingEventArgs",
            Decision = "unknown",
            ModelPath = model.Path,
            Phase = index < 8 ? "open" : index < 16 ? "snapshot" : "close",
            TimeUtc = "2026-09-30T00:00:00Z"
        });
        foreach (var phaseRecords in records.Chunk(8)) BatchDialogPolicy.Append(model, phaseRecords);
        await Assert.That(model.Dialogs.Count).IsEqualTo(20);
        await Assert.That(string.Join(",", model.Dialogs.Select(record => record.DialogId))).IsEqualTo(string.Join(",", Enumerable.Range(0, 20)));
        await Assert.That(string.Join(",", model.Dialogs.Select(record => record.Phase))).IsEqualTo(
            string.Join(",", Enumerable.Repeat("open", 8).Concat(Enumerable.Repeat("snapshot", 8)).Concat(Enumerable.Repeat("close", 4))));
    }

    [Test]
    [Arguments("open")]
    [Arguments("snapshot")]
    [Arguments("close")]
    public async Task PhaseResponseSerializesDialogsForSuccessAndFailure(string phase)
    {
        var record = new BatchDialogRecord
        {
            DialogId = "x",
            Type = "TaskDialogShowingEventArgs",
            Message = "Review model.",
            Decision = "unknown",
            ModelPath = @"C:\Models\A.rvt",
            Phase = phase,
            TimeUtc = "2026-09-30T00:00:00Z"
        };
        foreach (var success in new[] { true, false })
        {
            var command = $"batch-{phase}";
            var reason = "Unknown dialog 'x': Review model.";
            var data = new BatchPhaseResult<string> { Dialogs = [record], Result = success ? "phase result" : null };
            var response = success
                ? CommandResponse<BatchPhaseResult<string>>.Ok(command, data, 1)
                : CommandResponse<BatchPhaseResult<string>>.Fail(command, reason, 1);
            response.Data = data;
            using var json = System.Text.Json.JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
            await Assert.That(json.RootElement.GetProperty("success").GetBoolean()).IsEqualTo(success);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("dialogs")[0].GetProperty("phase").GetString()).IsEqualTo(phase);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("dialogs")[0].GetProperty("dialogId").GetString()).IsEqualTo("x");
            if (!success) await Assert.That(json.RootElement.GetProperty("message").GetString()).IsEqualTo(reason);
        }
    }

    private static string WriteAllowlist(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"batch-dialogs-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

}
