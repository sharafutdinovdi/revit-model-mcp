using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.BatchSupervisor;
using RevitModelMcp.Core.Batch;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchDialogPolicyTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SaveReminder_OnlyCancelsWithPendingWork(bool pending)
    {
        await Assert.That(BatchDialogPolicy.IsSaveReminder("TaskDialog_Project_Not_Saved_Recently",
            "TaskDialogShowingEventArgs", pending)).IsEqualTo(pending);
    }

    [Test]
    [Arguments("TaskDialog_Project_Not_Saved_Recently", "MessageBoxShowingEventArgs")]
    [Arguments("TaskDialog_Project_Not_Saved_Recently", "DialogBoxShowingEventArgs")]
    [Arguments("TaskDialog_Project_Not_Saved_Recently_Other", "TaskDialogShowingEventArgs")]
    [Arguments("Project Not Saved Recently", "TaskDialogShowingEventArgs")]
    [Arguments(null, "TaskDialogShowingEventArgs")]
    public async Task SaveReminder_DoesNotMatchOtherDialogs(string? dialogId, string type)
    {
        await Assert.That(BatchDialogPolicy.IsSaveReminder(dialogId, type)).IsFalse();
    }

    [Test]
    public async Task SaveReminder_CancelCannotBeReplacedWithSaveOrReminderSettings()
    {
        var choices = new Dictionary<(string Id, string Type), int>
        {
            [("TaskDialog_Project_Not_Saved_Recently", "TaskDialogShowingEventArgs")] = 1001
        };
        var decision = BatchDialogPolicy.Decide("TaskDialog_Project_Not_Saved_Recently",
            "TaskDialogShowingEventArgs", choices);
        await Assert.That(decision.Allowed).IsTrue();
        await Assert.That(decision.OverrideResult).IsEqualTo(2);
        await Assert.That(decision.Recycle).IsFalse();
        await Assert.That(BatchDialogPolicy.SaveReminderDismissed)
            .IsEqualTo("Dismissed Revit's save reminder without saving");
    }

    [Test]
    public async Task FamilyAudit_PartialResponsePreservesFamiliesAndDismissedDialogs()
    {
        var data = new FamilyAuditData
        {
            Mode = "project",
            Families = [new FamilyAuditFamily { Name = "Door" }],
            SkippedCount = 1,
            Skipped = [new SkippedRead
            {
                What = "Window",
                Reason = "Revit interruption suppressed during family access.",
                DialogId = "Dialog_Revit_Warning",
                Message = "Constraints between geometry in the family can behave unpredictably."
            }]
        };
        using var json = System.Text.Json.JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<FamilyAuditData>.PartialResult("family-audit", data, "Read budget reached.", 60_000)));
        var root = json.RootElement;
        await Assert.That(root.GetProperty("partial").GetBoolean()).IsTrue();
        var audit = root.GetProperty("data");
        await Assert.That(audit.GetProperty("families")[0].GetProperty("name").GetString()).IsEqualTo("Door");
        await Assert.That(audit.GetProperty("skippedCount").GetInt32()).IsEqualTo(1);
        var skipped = audit.GetProperty("skipped")[0];
        await Assert.That(skipped.GetProperty("what").GetString()).IsEqualTo("Window");
        await Assert.That(skipped.GetProperty("dialogId").GetString()).IsEqualTo("Dialog_Revit_Warning");
        await Assert.That(skipped.GetProperty("message").GetString())
            .IsEqualTo("Constraints between geometry in the family can behave unpredictably.");
    }

    [Test]
    [Arguments("TaskDialogShowingEventArgs", 0, "2,1")]
    [Arguments("DialogBoxShowingEventArgs", 0, "2,1")]
    [Arguments("MessageBoxShowingEventArgs", 0, "1")]
    [Arguments("MessageBoxShowingEventArgs", 1, "2")]
    [Arguments("MessageBoxShowingEventArgs", 2, "3")]
    [Arguments("MessageBoxShowingEventArgs", 3, "2")]
    [Arguments("MessageBoxShowingEventArgs", 4, "7")]
    [Arguments("MessageBoxShowingEventArgs", 5, "2")]
    [Arguments("MessageBoxShowingEventArgs", 6, "2")]
    [Arguments("MessageBoxShowingEventArgs", 0x104, "7")]
    public async Task FamilyAuditDialogResults_PrefersCancelWithoutApprovingChanges(string type, int buttons, string expected)
    {
        await Assert.That(string.Join(",", BatchDialogPolicy.FamilyAuditDialogResults(type, buttons))).IsEqualTo(expected);
    }

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
    public async Task Load_AcceptsUtf8BomBeforeExactPair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"batch-dialogs-{Guid.NewGuid():N}.json");
        var json = """[{"dialogId":"known","type":"TaskDialogShowingEventArgs","result":1}]""";
        try
        {
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]);
            var choices = BatchDialogPolicy.Load(path);
            await Assert.That(BatchDialogPolicy.Decide("known", "TaskDialogShowingEventArgs", choices).OverrideResult).IsEqualTo(1);
            await Assert.That(BatchDialogPolicy.Decide("known", "DialogBoxShowingEventArgs", choices).Allowed).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task RunStore_ReadsBomPrefixedRunAndLaunchFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"batch-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "run.json"),
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"runId":"known","status":0,"models":[]}""")]);
            File.WriteAllBytes(Path.Combine(directory, "launch.json"),
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"executables":{"2026":"Revit.exe"}}""")]);
            var store = new BatchRunStore(directory);
            await Assert.That(store.Read().RunId).IsEqualTo("known");
            await Assert.That(store.ReadLaunch().Executables[2026]).IsEqualTo("Revit.exe");
        }
        finally { Directory.Delete(directory, true); }
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

    [Test]
    [Arguments("Warning", false)]
    [Arguments("Error", true)]
    [Arguments("DocumentCorruption", true)]
    public async Task CallFailures_RollBackEveryNonWarning(string severity, bool expected)
    {
        await Assert.That(CallFailurePolicy.MustRollBack([new CallFailureRecord { Severity = severity }]))
            .IsEqualTo(expected);
        await Assert.That(CallFailurePolicy.MustRollBack([])).IsFalse();
    }

    [Test]
    public async Task CodeResponse_PreservesFailuresDialogsAndReturnValueOnSuccessAndFailure()
    {
        foreach (var success in new[] { true, false })
        {
            var data = new ActionResultData
            {
                ReturnValue = 42,
                Failures = [new CallFailureRecord
                {
                    Severity = success ? "Warning" : "Error",
                    Text = "Constraint failure.",
                    FailureDefinitionId = "failure-id",
                    ElementIds = [12, 34]
                }],
                Dialogs = [new BatchDialogRecord
                {
                    DialogId = "Review", Type = "TaskDialogShowingEventArgs", Message = "Review changes.",
                    Buttons = [new BatchDialogButton { Caption = "Cancel", Result = 2 }],
                    Decision = "unknown", ModelPath = "", Phase = "execute", TimeUtc = "2026-11-07T00:00:00Z"
                }]
            };
            var response = success
                ? CommandResponse<ActionResultData>.Ok("execute-code", data, 1)
                : CommandResponse<ActionResultData>.Fail("execute-code", "Constraint failure.", 1);
            response.Data = data;
            using var json = System.Text.Json.JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
            var result = json.RootElement.GetProperty("data");
            await Assert.That(result.GetProperty("returnValue").GetInt32()).IsEqualTo(42);
            var failure = result.GetProperty("failures")[0];
            await Assert.That(failure.GetProperty("text").GetString()).IsEqualTo("Constraint failure.");
            await Assert.That(failure.GetProperty("severity").GetString()).IsEqualTo(success ? "Warning" : "Error");
            await Assert.That(failure.GetProperty("failureDefinitionId").GetString()).IsEqualTo("failure-id");
            await Assert.That(failure.GetProperty("elementIds")[1].GetInt64()).IsEqualTo(34);
            var dialog = result.GetProperty("dialogs")[0];
            await Assert.That(dialog.GetProperty("dialogId").GetString()).IsEqualTo("Review");
            await Assert.That(dialog.GetProperty("message").GetString()).IsEqualTo("Review changes.");
            await Assert.That(dialog.GetProperty("buttons")[0].GetProperty("result").GetInt32()).IsEqualTo(2);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScopedHandlers_DetachAfterNestedExceptionOrCancellation(bool cancelled)
    {
        var subscriptions = 0;
        var removals = 0;
        ScopedCallHandlers Subscribe() => new(() => subscriptions++, () => { subscriptions--; removals++; });
        using (var outer = Subscribe())
        {
            try
            {
                using var inner = Subscribe();
                await Assert.That(subscriptions).IsEqualTo(2);
                if (cancelled) throw new OperationCanceledException();
                throw new InvalidOperationException();
            }
            catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException) { }
            await Assert.That(subscriptions).IsEqualTo(1);
            outer.Dispose();
            outer.Dispose();
        }
        await Assert.That(subscriptions).IsEqualTo(0);
        await Assert.That(removals).IsEqualTo(2);
    }

    private static string WriteAllowlist(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"batch-dialogs-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

}
