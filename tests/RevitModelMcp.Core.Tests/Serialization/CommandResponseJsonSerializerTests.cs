using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using RevitModelMcp.Core.Batch;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Serialization;

public sealed class CommandResponseJsonSerializerTests
{
    [Test]
    public async Task ProcessModels_SerializesDoneFailedAndSkippedModels()
    {
        var data = new ActionResultData
        {
            Total = 3,
            Done = 1,
            Failed = 1,
            SkippedCount = 1,
            Summary = "Processed 1 of 3 models; 1 failed, 1 skipped.",
            Models =
            [
                new ProcessModelResult
                {
                    Path = @"C:\Models\Done.rvt", Status = "done",
                    Opened = new ProcessModelOpenedResult { Mode = "detached" },
                    Code = new ProcessModelCodeResult { ReturnValue = 42, ReturnValueMarker = "return-marker", Log = ["ran"] },
                    Exports = [new ActionResultData { Folder = @"C:\Out\Done", Files = [new ExportedFile { Name = "Done.ifc", SizeBytes = 10 }] }],
                    Saved = @"C:\Out\Done.rvt",
                    DialogsDismissed = ProcessDialogSummary.FromMessages(["Space warning", "Space warning", "Other warning"])
                },
                new ProcessModelResult { Path = @"C:\Models\Missing.rvt", Status = "failed", Error = "File not found." },
                new ProcessModelResult { Path = @"C:\Models\Open.rvt", Status = "skipped", Error = "Already open." }
            ]
        };

        using var json = Parse(CommandResponse<ActionResultData>.PartialResult("process-models", data, data.Summary!, 5));
        var models = json.RootElement.GetProperty("data").GetProperty("models");
        await Assert.That(models.GetArrayLength()).IsEqualTo(3);
        await Assert.That(models[0].GetProperty("code").GetProperty("returnValue").GetInt32()).IsEqualTo(42);
        await Assert.That(models[0].GetProperty("exports")[0].GetProperty("files")[0].GetProperty("name").GetString()).IsEqualTo("Done.ifc");
        await Assert.That(models[0].GetProperty("saved").GetString()).IsEqualTo(@"C:\Out\Done.rvt");
        var dismissed = models[0].GetProperty("dialogsDismissed");
        await Assert.That(dismissed.GetProperty("messages")[0].GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(dismissed.GetProperty("truncated").GetBoolean()).IsFalse();
        await Assert.That(models[1].GetProperty("error").GetString()).IsEqualTo("File not found.");
        await Assert.That(models[1].GetProperty("dialogsDismissed").GetProperty("messages").GetArrayLength()).IsEqualTo(0);
        await Assert.That(models[2].GetProperty("status").GetString()).IsEqualTo("skipped");
    }

    [Test]
    public async Task ProcessModels_DismissedMessagesLimitDistinctEntries()
    {
        var summary = ProcessDialogSummary.FromMessages(
            Enumerable.Range(0, 51).Select(index => $"Warning {index}").Concat(["Warning 0"]));

        await Assert.That(summary.Messages.Count).IsEqualTo(50);
        await Assert.That(summary.Messages[0].Count).IsEqualTo(2);
        await Assert.That(summary.Truncated).IsTrue();
    }

    [Test]
    public async Task ExecuteCode_SerializesLimitedJsonValuesAndDiagnostics()
    {
        var response = CommandResponse<ActionResultData>.Ok("execute-code", new ActionResultData
        {
            ReturnValue = CodeResultLimiter.Limit(new Dictionary<string, object?>
            {
                ["count"] = 3,
                ["items"] = new[] { "a", "b" }
            }, value => value),
            Log = ["ready"],
            Diagnostics = [new CodeDiagnostic { Line = 2, Column = 4, Id = "CS1002", Message = "; expected" }],
            Summary = "Executed code in Model.rvt."
        }, 5);
        var json = CommandResponseJsonSerializer.Serialize(response);
        await Assert.That(json.Contains("\"returnValue\"")).IsTrue();
        await Assert.That(json.Contains("\"count\":3")).IsTrue();
        await Assert.That(json.Contains("\"line\":2")).IsTrue();
    }

    [Test]
    public async Task Serialize_CreatedViewsSheetsAndPlacements_ExposeTopLevelIds()
    {
        var view = new ActionResultData
        {
            ViewId = 42,
            ViewName = "Night section",
            Id = 42,
            Verification = new ActionVerification { After = new ActionFacts { Id = 42 } }
        };
        using var viewJson = Parse(CommandResponse<ActionResultData>.Ok("create-view", view, 1));
        var viewData = viewJson.RootElement.GetProperty("data");
        await Assert.That(viewData.GetProperty("viewId").GetInt64()).IsEqualTo(42);
        await Assert.That(viewData.GetProperty("viewName").GetString()).IsEqualTo("Night section");
        await Assert.That(viewData.TryGetProperty("view", out _)).IsFalse();
        await Assert.That(viewData.GetProperty("verification").GetProperty("after").GetProperty("id").GetInt64()).IsEqualTo(42);

        var sheet = new ActionResultData { SheetId = 51, SheetNumber = "NX-101", SheetName = "Night sheet" };
        using var sheetJson = Parse(CommandResponse<ActionResultData>.Ok("create-sheet", sheet, 1));
        var sheetData = sheetJson.RootElement.GetProperty("data");
        await Assert.That(sheetData.GetProperty("sheetId").GetInt64()).IsEqualTo(51);
        await Assert.That(sheetData.GetProperty("sheetNumber").GetString()).IsEqualTo("NX-101");
        await Assert.That(sheetData.GetProperty("sheetName").GetString()).IsEqualTo("Night sheet");

        var placements = new ActionResultData { ViewportIds = [61, 62], ScheduleInstanceIds = [63] };
        using var placementJson = Parse(CommandResponse<ActionResultData>.Ok("place-views-on-sheet", placements, 1));
        var placementData = placementJson.RootElement.GetProperty("data");
        await Assert.That(placementData.GetProperty("viewportIds").EnumerateArray().Select(id => id.GetInt64())).IsEquivalentTo(new long[] { 61, 62 });
        await Assert.That(placementData.GetProperty("scheduleInstanceIds")[0].GetInt64()).IsEqualTo(63);
    }

    [Test]
    public async Task Serialize_ModelSnapshot_UsesExactSchemaNamesAndNulls()
    {
        var snapshot = new ModelSnapshotData
        {
            CollectedAtUtc = "2026-09-30T12:00:00Z",
            Source = new ModelSnapshotSource { Path = @"C:\Models\Model.rvt", RuntimeYear = 2026 },
            Passport = new ModelSnapshotPassport
            {
                Title = "Model",
                RevitServer = null,
                Counts = new()
                {
                    ["elements"] = 0,
                    ["views"] = 0,
                    ["sheets"] = 0,
                    ["families"] = 0,
                    ["familyTypes"] = 0,
                    ["links"] = 0
                }
            },
            Warnings = new ModelSnapshotWarnings
            {
                Total = 201,
                Groups =
                [ModelSnapshotWarningGroup.Create("Warning", 201, Enumerable.Range(1, 201).Select(id => (long)id))]
            },
            Skipped = [new SkippedRead { What = "source file", Reason = "Unavailable" }],
            SkippedCount = 1
        };
        using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<ModelSnapshotData>.Ok("model-snapshot", snapshot, 1)));
        var data = json.RootElement.GetProperty("data");
        await Assert.That(data.EnumerateObject().Select(property => property.Name).Order().ToArray())
            .IsEquivalentTo(new[] { "schemaVersion", "collectedAtUtc", "source", "passport", "warnings",
                "families", "parameterFill", "skipped", "skippedCount" });
        await Assert.That(data.GetProperty("schemaVersion").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("source").EnumerateObject().Select(property => property.Name).Order().ToArray())
            .IsEquivalentTo(new[] { "path", "kind", "runtimeYear", "savedInYear", "upgradedInMemory" });
        await Assert.That(data.GetProperty("passport").EnumerateObject().Select(property => property.Name).Order().ToArray())
            .IsEquivalentTo(new[] { "title", "isWorkshared", "centralPath", "numberOfSaves", "versionGuid",
                "basicFileInfoUsername", "fileLastWriteUtc", "fileSizeBytes", "revitServer", "counts", "worksets" });
        await Assert.That(data.GetProperty("warnings").EnumerateObject().Select(property => property.Name).Order().ToArray())
            .IsEquivalentTo(new[] { "total", "groups" });
        await Assert.That(data.GetProperty("families").EnumerateObject().Select(property => property.Name).Order().ToArray())
            .IsEquivalentTo(new[] { "total", "inPlace", "nonEditable", "items", "signals" });
        await Assert.That(data.GetProperty("passport").GetProperty("revitServer").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(data.GetProperty("warnings").GetProperty("groups")[0].GetProperty("elementIds").GetArrayLength()).IsEqualTo(200);
        await Assert.That(data.GetProperty("warnings").GetProperty("groups")[0].GetProperty("elementIdsTruncated").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("parameterFill").GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("skipped")[0].GetProperty("what").GetString()).IsEqualTo("source file");
        await Assert.That(data.GetProperty("skippedCount").GetInt32()).IsEqualTo(1);
        await Assert.That(RoundTripCoordinator("model-snapshot", snapshot).Passport.RevitServer).IsNull();
    }

    [Test]
    public async Task ModelSnapshotWarningGroup_CapsDistinctElementIdsAt201stId()
    {
        var firstTwoHundred = Enumerable.Range(1, 200).Reverse().Select(id => (long)id).ToArray();
        var atLimit = ModelSnapshotWarningGroup.Create("Warning", 1, firstTwoHundred.Append(1).Append(200));
        var overLimit = ModelSnapshotWarningGroup.Create("Warning", 1,
            new long[] { 200, 200 }.Concat(firstTwoHundred).Append(1).Append(201).Append(202));

        await Assert.That(atLimit.ElementIds.SequenceEqual(firstTwoHundred)).IsTrue();
        await Assert.That(atLimit.ElementIdsTruncated).IsFalse();
        await Assert.That(overLimit.ElementIds.SequenceEqual(firstTwoHundred)).IsTrue();
        await Assert.That(overLimit.ElementIdsTruncated).IsTrue();
    }

    private static T RoundTripCoordinator<T>(string command, T data)
    {
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<T>.Ok(command, data, 1));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(
            typeof(CommandResponse<T>), new System.Runtime.Serialization.Json.DataContractJsonSerializerSettings
            { UseSimpleDictionaryFormat = true });
        return ((CommandResponse<T>)serializer.ReadObject(stream)!).Data!;
    }

    [Test]
    public async Task Serialize_BatchSupervisorStart_RoundTripsRunId()
    {
        var result = RoundTripCoordinator("batch-supervisor-start", new BatchStartResult
        {
            RunId = "b6834588269d44b6985a6721f333573d"
        });

        await Assert.That(result.RunId).IsEqualTo("b6834588269d44b6985a6721f333573d");
    }

    [Test]
    public async Task Serialize_BatchRun_RoundTripsStatusCancelAndFetchState()
    {
        var dialog = new BatchDialogRecord
        {
            DialogId = "missing-link",
            Type = "TaskDialogShowingEventArgs",
            Message = "Link unavailable",
            Decision = "unknown",
            ModelPath = @"C:\Models\A.rvt",
            Phase = "snapshot",
            TimeUtc = "2026-09-30T12:00:00Z"
        };
        var run = new BatchRun
        {
            RunId = "run-1",
            Status = BatchRunStatus.Running,
            Years = [2026],
            Models = [new BatchModel
            {
                Path = @"C:\Models\A.rvt", Status = BatchModelStatus.Running,
                Phase = BatchPhase.Snapshot, SavedYear = 2025,
                PhaseTimingsMs = new() { ["open"] = 42 }, Dialogs = [dialog],
                SnapshotFile = "snapshot.json"
            }]
        };

        var status = RoundTripCoordinator("batch-status", run);
        await Assert.That(status.Status).IsEqualTo(BatchRunStatus.Running);
        await Assert.That(status.Models[0].PhaseTimingsMs["open"]).IsEqualTo(42);
        await Assert.That(status.Models[0].Dialogs[0].Message).IsEqualTo("Link unavailable");

        var cancelled = RoundTripCoordinator("batch-cancel", run with
        {
            Status = BatchRunStatus.Cancelled,
            CancelRequested = true
        });
        await Assert.That(cancelled.CancelRequested).IsTrue();
        await Assert.That(cancelled.Status).IsEqualTo(BatchRunStatus.Cancelled);

        var fetched = RoundTripCoordinator("batch-fetch", run with { Status = BatchRunStatus.Completed });
        await Assert.That(fetched.Models[0].SnapshotFile).IsEqualTo("snapshot.json");
        await Assert.That(fetched.Models[0].Dialogs[0].DialogId).IsEqualTo("missing-link");
    }

    [Test]
    public async Task Serialize_BatchPhases_RoundTripConcreteResultsAndDialogs()
    {
        var open = RoundTripCoordinator("batch-open", new BatchPhaseResult<ActionResultData>
        {
            Result = new ActionResultData { Title = "Model A", OpenedAs = "detached" }
        });
        await Assert.That(open.Result!.Title).IsEqualTo("Model A");

        var close = RoundTripCoordinator("batch-close", new BatchPhaseResult<ActionResultData>
        {
            Result = new ActionResultData { Saved = false, Path = @"C:\Models\A.rvt" }
        });
        await Assert.That(close.Result!.Saved).IsFalse();

        var snapshot = RoundTripCoordinator("batch-snapshot", new BatchPhaseResult<ModelSnapshotData>
        {
            Result = new ModelSnapshotData
            {
                CollectedAtUtc = "2026-09-30T12:00:00Z",
                Source = new ModelSnapshotSource { RuntimeYear = 2026 },
                Passport = new ModelSnapshotPassport { Title = "Model A" }
            },
            Dialogs = [new BatchDialogRecord
            {
                DialogId = "warning", Type = "TaskDialogShowingEventArgs", Decision = "allowed",
                ModelPath = @"C:\Models\A.rvt", Phase = "snapshot", TimeUtc = "2026-09-30T12:00:00Z",
                Result = 1001
            }]
        });
        await Assert.That(snapshot.Result!.Source.RuntimeYear).IsEqualTo(2026);
        await Assert.That(snapshot.Result.Passport.Title).IsEqualTo("Model A");
        await Assert.That(snapshot.Dialogs[0].Result).IsEqualTo(1001);
        await Assert.That(snapshot.Dialogs[0].Phase).IsEqualTo("snapshot");
    }

    [Test]
    public async Task Serialize_DocumentChangedError_PreservesCorrelationAndResponder()
    {
        var response = CommandResponse<object>.Fail("document-info", "The active document no longer matches the target document.", 0, "read-50");
        response.Responder = new ResponderInfo { ProcessId = 42, DocumentName = "Architectural" };
        using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
        await Assert.That(json.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(json.RootElement.GetProperty("correlationId").GetString()).IsEqualTo("read-50");
        await Assert.That(json.RootElement.GetProperty("responder").GetProperty("processId").GetInt32()).IsEqualTo(42);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Serialize_RoundTripsCorrelationId(bool partial)
    {
        var response = partial
            ? CommandResponse<string>.PartialResult("ping", "accepted", "Command accepted and running.", 0)
            : CommandResponse<string>.Ok("ping", "pong", 1);
        response.CorrelationId = "job-24";
        var content = CommandResponseJsonSerializer.Serialize(response);
        using var json = JsonDocument.Parse(content);
        await Assert.That(json.RootElement.GetProperty("correlationId").GetString()).IsEqualTo("job-24");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var restored = (CommandResponse<string>)new DataContractJsonSerializer(typeof(CommandResponse<string>)).ReadObject(stream)!;
        await Assert.That(restored.CorrelationId).IsEqualTo(response.CorrelationId);
        await Assert.That(restored.Partial).IsEqualTo(partial);
    }

    [Test]
    public async Task CreatePath_CorrelatedJob_ReusesFilenameWhileLegacyUsesSuffix()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RevitModelMcp-tests-{Guid.NewGuid():N}");
        var timestamp = new DateTime(2026, 9, 16, 12, 34, 56, 789);
        Directory.CreateDirectory(directory);
        try
        {
            var path = CommandResponseJsonFile.CreatePath(directory, timestamp, "ping", "job-24");
            await Assert.That(Path.GetFileName(path)).IsEqualTo("response_20260916_123456_789_ping_job-24.json");
            File.WriteAllText(path, "existing");
            await Assert.That(CommandResponseJsonFile.CreatePath(directory, timestamp, "ping", "job-24")).IsEqualTo(path);
            var other = CommandResponseJsonFile.CreatePath(directory, timestamp, "ping", "other-job");
            await Assert.That(other).IsNotEqualTo(path);
            var legacy = CommandResponseJsonFile.CreatePath(directory, timestamp, "ping");
            await Assert.That(Path.GetFileName(legacy)).IsEqualTo("response_20260916_123456_789_ping.json");
            File.WriteAllText(legacy, "existing");
            var nextLegacy = CommandResponseJsonFile.CreatePath(directory, timestamp, "ping");
            await Assert.That(Path.GetFileName(nextLegacy)).IsEqualTo("response_20260916_123456_789_ping_01.json");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Write_ReplacesProgressAtomicallyAndCleansTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RevitModelMcp-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "response_ping_job-24.json");
        try
        {
            var progress = CommandResponse<string>.PartialResult("ping", new string('x', 100_000), "Processing.", 1);
            progress.CorrelationId = "job-24";
            CommandResponseJsonFile.Write(path, progress);
            var writer = Task.Run(() =>
            {
                for (var iteration = 0; iteration < 100; iteration++)
                    CommandResponseJsonFile.Write(path, progress);
                var terminal = CommandResponse<string>.Ok("ping", "pong", 2);
                terminal.CorrelationId = "job-24";
                CommandResponseJsonFile.Write(path, terminal);
            });
            while (!writer.IsCompleted)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var json = JsonDocument.Parse(stream);
                    await Assert.That(json.RootElement.GetProperty("correlationId").GetString()).IsEqualTo("job-24");
                }
                catch (IOException)
                {
                    continue;
                }
            }
            await writer;
            using var final = JsonDocument.Parse(File.ReadAllText(path));
            await Assert.That(final.RootElement.GetProperty("partial").GetBoolean()).IsFalse();
            await Assert.That(final.RootElement.GetProperty("data").GetString()).IsEqualTo("pong");
            await Assert.That(Directory.GetFiles(directory, "*.tmp").Length).IsEqualTo(0);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < 9)
                    Thread.Sleep(5);
            }
        }
    }

    [Test]
    public async Task CoordinatorHealth_RoundTrip_PreservesNullMetrics()
    {
        var data = RoundTripCoordinator("model-health", new ModelHealthData
        {
            Counts = { ["elements"] = 12, ["rooms"] = null },
            TopWarnings = [new HealthWarning { Text = "Warning", Count = 2 }]
        });
        await Assert.That(data.Counts["elements"]).IsEqualTo(12);
        await Assert.That(data.Counts["rooms"]).IsNull();
        await Assert.That(data.FileSizeBytes).IsNull();
        await Assert.That(data.TopWarnings[0].Count).IsEqualTo(2);
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("model health counts.rooms", new InvalidOperationException("Unavailable"));
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<ModelHealthData>.Ok("model-health", data, 1)));
            await Assert.That(json.RootElement.GetProperty("skipped")[0].GetProperty("what").GetString())
                .IsEqualTo("model health counts.rooms");
        }
        finally
        {
            SkippedReadDiagnostics.Current = null;
        }
    }

    [Test]
    public async Task CoordinatorLinks_RoundTrip_PreservesStatusAndPath()
    {
        var data = RoundTripCoordinator("links-status", new LinksStatusData
        {
            RvtLinks = [new RvtLinkStatus { Name = "A", TypeId = 1, Status = "Unloaded", Path = @"C:\Models\A.rvt" }],
            CadLinks = [new CadLinkStatus { IsLinked = false, Instances = 2 }],
            Images = [new ImageLinkStatus { Status = "Other", Error = "Unavailable" }],
            Summary = new LinkSummary { Rvt = 1, CadImports = 2 }
        });
        await Assert.That(data.RvtLinks[0].Path).IsEqualTo(@"C:\Models\A.rvt");
        await Assert.That(data.CadLinks[0].IsLinked).IsFalse();
        await Assert.That(data.Images[0].Error).IsEqualTo("Unavailable");
        await Assert.That(data.Summary.CadImports).IsEqualTo(2);
    }

    [Test]
    public async Task CoordinatorCoordinates_RoundTrip_PreservesOffsets()
    {
        var data = RoundTripCoordinator("shared-coordinates", new SharedCoordinatesData
        {
            ProjectLocations = ["Site"],
            TrueNorthAngleDeg = 30.1,
            ProjectBasePoint = new CoordinatePoint { EastWestMm = 12.3 },
            SharedSiteFromLinks = [new LinkSharedSite { HasOffset = true, OffsetMm = new CoordinateOffset { X = 100 } }]
        });
        await Assert.That(data.TrueNorthAngleDeg).IsEqualTo(30.1);
        await Assert.That(data.ProjectBasePoint.Clipped).IsNull();
        await Assert.That(data.SharedSiteFromLinks[0].OffsetMm.X).IsEqualTo(100);
        await Assert.That(data.SharedSiteFromLinks[0].SharedSiteName).IsNull();
    }

    [Test]
    public async Task CoordinatorFill_RoundTrip_PreservesCountersAndSamples()
    {
        var data = RoundTripCoordinator("parameter-fill-check", new ParameterFillData
        {
            Scope = new ParameterFillScope { Categories = ["Walls"], Elements = 3 },
            Parameters = [new ParameterFillItem
            {
                Name = "Mark", Elements = 3, Filled = 1, Empty = 1, Missing = 1,
                StorageTypes = { ["String"] = 2 }, Owner = new ParameterOwnerCounts { Instance = 1, Type = 1 },
                EmptySampleIds = [12], MissingSampleIds = [13],
                ByCategory = [new ParameterCategoryFill { Category = "Walls", Elements = 3, Filled = 1, Empty = 1, Missing = 1 }]
            }]
        });
        await Assert.That(data.Parameters[0].StorageTypes["String"]).IsEqualTo(2);
        await Assert.That(data.Parameters[0].Owner.Type).IsEqualTo(1);
        await Assert.That(data.Parameters[0].MissingSampleIds[0]).IsEqualTo(13);
        await Assert.That(data.Parameters[0].ByCategory[0].Empty).IsEqualTo(1);
    }

    [Test]
    public async Task Serialize_PostCommitVerificationError_RoundTripsErrorAndOmitsAfter()
    {
        var error = "Post-commit verification failed: Element is unavailable.";
        var response = CommandResponse<ActionResultData>.Ok("move", new ActionResultData
        {
            Verification = new ActionVerification { Error = error, After = null }
        }, 1);
        var serialized = CommandResponseJsonSerializer.Serialize(response);
        using var json = JsonDocument.Parse(serialized);
        var verification = json.RootElement.GetProperty("data").GetProperty("verification");
        await Assert.That(json.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(verification.GetProperty("error").GetString()).IsEqualTo(error);
        await Assert.That(verification.TryGetProperty("after", out _)).IsFalse();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(serialized));
        var serializer = new DataContractJsonSerializer(typeof(CommandResponse<ActionResultData>));
        var restored = (CommandResponse<ActionResultData>)serializer.ReadObject(stream)!;
        await Assert.That(restored.Success).IsTrue();
        await Assert.That(restored.Data!.Verification!.Error).IsEqualTo(error);
        await Assert.That(restored.Data.Verification.After).IsNull();
    }

    [Test]
    public async Task Serialize_VerifiedBatch_RoundTripsFactsAndFalseFlags()
    {
        var verification = new ActionVerification
        {
            Before = new ActionFacts { Id = 1, Parameter = "Comments", ParameterId = "ALL_MODEL_INSTANCE_COMMENTS", Value = "", StorageType = "String", Owner = "instance" },
            After = new ActionFacts { Id = 1, Parameter = "Comments", ParameterId = "ALL_MODEL_INSTANCE_COMMENTS", Value = "Reviewed", StorageType = "String", Owner = "instance" },
            Changed = [1]
        };
        var data = new ActionResultData
        {
            DryRun = false,
            Committed = false,
            FailedStep = 1,
            UndoName = "revit_batch",
            RolledBack = true,
            Steps = [
                new BatchStepResult { Index = 0, Command = "set-parameter", Success = true, RolledBack = true,
                    Data = new ActionResultData { DryRun = false, Verification = verification, RolledBack = true } },
                new BatchStepResult { Index = 1, Command = "delete", Success = false, Error = "Element not found." }
            ]
        };
        var response = CommandResponse<ActionResultData>.Ok("batch", data, 1);
        var serialized = CommandResponseJsonSerializer.Serialize(response);
        using var json = JsonDocument.Parse(serialized);
        var payload = json.RootElement.GetProperty("data");
        await Assert.That(payload.GetProperty("dryRun").GetBoolean()).IsFalse();
        await Assert.That(payload.GetProperty("committed").GetBoolean()).IsFalse();
        await Assert.That(payload.GetProperty("steps")[0].GetProperty("index").GetInt32()).IsEqualTo(0);
        await Assert.That(payload.GetProperty("steps")[0].GetProperty("data").GetProperty("verification").GetProperty("before").GetProperty("parameterId").GetString()).IsEqualTo("ALL_MODEL_INSTANCE_COMMENTS");
        await Assert.That(payload.GetProperty("steps")[0].GetProperty("data").GetProperty("verification").GetProperty("after").GetProperty("parameterId").GetString()).IsEqualTo("ALL_MODEL_INSTANCE_COMMENTS");
        await Assert.That(payload.GetProperty("steps")[1].GetProperty("success").GetBoolean()).IsFalse();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(serialized));
        var serializer = new DataContractJsonSerializer(typeof(CommandResponse<ActionResultData>));
        var restored = (CommandResponse<ActionResultData>)serializer.ReadObject(stream)!;
        await Assert.That(restored.Data!.Steps![0].Data!.Verification!.Before!.Value).IsEqualTo("");
        await Assert.That(restored.Data.Steps[0].Data!.Verification!.After!.Owner).IsEqualTo("instance");
        await Assert.That(restored.Data.Steps[0].Data!.Verification!.Changed!).IsEquivalentTo(new long[] { 1 });
        await Assert.That(restored.Data.Steps[1].Error).IsEqualTo("Element not found.");
        data.FailedStep = null;
        using var successJson = Parse(response);
        await Assert.That(successJson.RootElement.GetProperty("data").GetProperty("failedStep").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Serialize_DocumentInfo_PreservesEnvelopeAndData()
    {
        var response = CommandResponse<DocumentInfoData>.Ok(
            "document-info",
            new DocumentInfoData
            {
                FileName = "Sample Model.rvt",
                RevitVersion = "2024",
                IsWorkshared = true,
                ViewCount = 84,
                Levels = { new DocumentLevelInfo { Name = "Level 1", ElevationMm = 0, RoomCount = 12 } },
                AreaSchemes =
                {
                    new DocumentAreaSchemeInfo { Name = "Gross", IsGrossBuildingArea = true, AreaCount = 5 }
                },
                Worksets = { new DocumentWorksetInfo { Name = "Shared Levels and Grids", Kind = "UserWorkset", IsOpen = true } }
            },
            31);
        response.Responder = new ResponderInfo
        {
            DocumentName = "Sample Model.rvt",
            DocumentPath = @"C:\\Models\\Sample Model.rvt",
            ProcessId = 4242,
            RevitVersion = "2024"
        };

        using var json = Parse(response);

        await AssertSuccess(json.RootElement, "document-info");
        await Assert.That(json.RootElement.GetProperty("data").GetProperty("viewCount").GetInt32()).IsEqualTo(84);
        var responder = json.RootElement.GetProperty("responder");
        await Assert.That(responder.GetProperty("documentName").GetString()).IsEqualTo("Sample Model.rvt");
        await Assert.That(responder.GetProperty("processId").GetInt32()).IsEqualTo(4242);
    }

    [Test]
    public async Task Serialize_ListViews_PreservesPreparedView()
    {
        var response = CommandResponse<ViewListData>.Ok(
            "list-views",
            new ViewListData
            {
                Processed = 1,
                Total = 1,
                Views =
                {
                    new ViewListItem
                    {
                        Id = 11,
                        Name = "Level 1 Plan",
                        Type = "FloorPlan",
                        Level = "Level 1",
                        Scale = 100,
                        Template = "Floor Plan Template"
                    }
                }
            },
            45,
            "Element counts were not calculated.");

        using var json = Parse(response);

        await AssertSuccess(json.RootElement, "list-views");
        var data = json.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("elementPresenceMethod").GetString()).IsEqualTo("not-read");
        await Assert.That(data.GetProperty("processed").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("views")[0].TryGetProperty("hasElements", out _)).IsFalse();
    }

    [Test]
    public async Task Ping_CreatesSuccessWithoutDocumentData()
    {
        var response = ReadCommandResponseFactory.Ping(3);

        using var json = Parse(response);
        var root = json.RootElement;

        await AssertSuccess(root, "ping");
        await Assert.That(root.GetProperty("data").GetString()).IsEqualTo("pong");
        await Assert.That(root.GetProperty("elapsedMs").GetInt64()).IsEqualTo(3);
    }

    [Test]
    public async Task Serialize_ViewSummary_PreservesCategoriesWithoutElements()
    {
        var response = CommandResponse<ViewSummaryData>.Ok(
            "view-summary",
            new ViewSummaryData
            {
                Header = new ViewDumpHeader { Name = "Level 1 Plan", Type = "FloorPlan", Scale = 100, ElementCount = 21 },
                Categories = { new ViewCategorySummary { Category = "Walls", Count = 12, DifferentTypes = 3 } }
            },
            120);

        using var json = Parse(response);

        await AssertSuccess(json.RootElement, "view-summary");
        var data = json.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("categories")[0].GetProperty("count").GetInt32()).IsEqualTo(12);
        await Assert.That(data.TryGetProperty("elements", out _)).IsFalse();
    }

    [Test]
    public async Task Serialize_ViewElements_PreservesPagination()
    {
        var response = CommandResponse<ViewElementsData>.Ok(
            "view-elements",
            new ViewElementsData
            {
                View = "Level 1 Plan",
                Categories = { "Walls" },
                Offset = 10,
                Limit = 1,
                Total = 12,
                HasMore = true,
                Elements = { new ViewElementDump { Id = 11327511, Category = "Walls" } }
            },
            18);

        using var json = Parse(response);

        await AssertSuccess(json.RootElement, "view-elements");
        var data = json.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(12);
        await Assert.That(data.GetProperty("hasMore").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Serialize_ElementDetails_PreservesInstanceAndTypeParameters()
    {
        var response = CommandResponse<ElementDetailsData>.Ok(
            "element-details",
            new ElementDetailsData
            {
                Element = new ViewElementDump { Id = 11327511, Category = "Walls" },
                Parameters =
                {
                    new ElementParameterDetail
                    {
                        Name = "Length",
                        StorageType = "Double",
                        HasValue = true,
                        MetricValue = 2500,
                        MetricUnit = "mm"
                    }
                },
                TypeElement = new ElementTypeDetails
                {
                    Id = 42,
                    Family = "Basic Wall",
                    Name = "Wall Type A",
                    Parameters = { new ElementParameterDetail { Name = "Thickness", StorageType = "Double", HasValue = true } }
                },
                Warnings = { new ElementWarningInfo { Text = "Room is not enclosed.", Severity = "Warning" } },
                Room = new RoomDetails
                {
                    Level = "Level 1",
                    AreaM2 = 12.5,
                    VolumeM3 = 37.5,
                    Boundaries =
                    {
                        new RoomBoundaryLoop
                        {
                            Segments = { new RoomBoundarySegment { ElementId = 7, LengthMm = 2500 } }
                        }
                    }
                }
            },
            8);

        using var json = Parse(response);

        await AssertSuccess(json.RootElement, "element-details");
        var data = json.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("parameters")[0].GetProperty("metricValue").GetDouble()).IsEqualTo(2500);
        await Assert.That(data.GetProperty("typeElement").GetProperty("parameters").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("warnings").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("room").GetProperty("areaM2").GetDouble()).IsEqualTo(12.5);
    }

    [Test]
    public async Task Serialize_QueryElements_PreservesDynamicRequestedFields()
    {
        var response = CommandResponse<QueryElementsData>.Ok(
            "query-elements",
            new QueryElementsData
            {
                Offset = 0,
                Limit = 100,
                Total = 1,
                Fields = { "category", "Building Number" },
                Elements =
                {
                    new QueryElementItem
                    {
                        Id = 17,
                        Values =
                        {
                            ["category"] = new QueryFieldValue { HasValue = true, Value = "Rooms" },
                            ["Building Number"] = new QueryFieldValue { HasValue = false, Source = "instance" }
                        }
                    }
                }
            },
            12);

        using var json = Parse(response);
        var values = json.RootElement.GetProperty("data").GetProperty("elements")[0].GetProperty("values");

        await Assert.That(values.GetProperty("category").GetProperty("value").GetString()).IsEqualTo("Rooms");
        await Assert.That(values.GetProperty("Building Number").GetProperty("hasValue").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Serialize_AggregateWithoutNumericValues_ReportsResolvedField()
    {
        var response = CommandResponse<AggregateElementsData>.Ok(
            "aggregate-elements",
            new AggregateElementsData
            {
                MatchedElements = 1,
                GroupBy = { "level" },
                NumericField = "Area",
                NumericFieldFound = true,
                Groups =
                {
                    new AggregateGroup
                    {
                        Keys = { ["level"] = "Level 1" },
                        Count = 1,
                        NumericCount = 0
                    }
                }
            },
            9);

        using var json = Parse(response);
        var data = json.RootElement.GetProperty("data");

        await Assert.That(data.GetProperty("numericFieldFound").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("groups")[0].GetProperty("numericCount").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("groups")[0].TryGetProperty("sum", out _)).IsFalse();
    }

    [Test]
    public async Task Serialize_ViewWarnings_PreservesDocumentScopeMatch()
    {
        var response = CommandResponse<ViewWarningsData>.Ok(
            "view-warnings",
            new ViewWarningsData
            {
                View = "Level 1 Plan",
                MatchingNote = "Matching uses elements.",
                Warnings =
                {
                    new ViewWarningInfo
                    {
                        Text = "Highlighted walls overlap.",
                        Severity = "Warning",
                        HasElementsOnView = true,
                        Elements = { new ViewWarningElementInfo { Id = 17, PresentOnView = true } }
                    }
                }
            },
            16);

        using var json = Parse(response);

        await AssertSuccess(json.RootElement, "view-warnings");
        var data = json.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("scope").GetString()).IsEqualTo("view-elements");
        await Assert.That(data.GetProperty("warnings")[0]
            .GetProperty("hasElementsOnView").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Serialize_ExportView_PreservesFileAndViewMetadata()
    {
        var response = CommandResponse<ViewExportData>.Ok(
            "export-view",
            new ViewExportData
            {
                FileName = "view_20260817_120000_000_42.png",
                Width = 1600,
                Height = 900,
                SizeBytes = 123456,
                ViewName = "Level 1 Plan",
                ViewType = "FloorPlan"
            },
            812);

        using var json = Parse(response);
        var data = json.RootElement.GetProperty("data");

        await AssertSuccess(json.RootElement, "export-view");
        await Assert.That(data.GetProperty("width").GetInt32()).IsEqualTo(1600);
        await Assert.That(data.GetProperty("sizeBytes").GetInt64()).IsEqualTo(123456);
        await Assert.That(data.GetProperty("viewType").GetString()).IsEqualTo("FloorPlan");
    }

    [Test]
    public async Task Serialize_MissingView_ReturnsReadableFailureWithoutData()
    {
        var response = CommandResponse<ViewSummaryData>.ViewNotFound("view-summary", "Missing View", 2);

        using var json = Parse(response);
        var root = json.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("message").GetString()).Contains("was not found");
        await Assert.That(root.TryGetProperty("data", out _)).IsFalse();
    }

    [Test]
    public async Task Serialize_InvalidElementId_ReturnsReadableFailureWithoutData()
    {
        var response = CommandResponse<ElementDetailsData>.ElementNotFound("element-details", 999, 1);

        using var json = Parse(response);
        var root = json.RootElement;

        await Assert.That(root.GetProperty("command").GetString()).IsEqualTo("element-details");
        await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("message").GetString()).Contains("999");
    }

    [Test]
    public async Task Serialize_PartialResult_MarksEnvelopeAndPreservesData()
    {
        var response = CommandResponse<ViewElementsData>.PartialResult(
            "view-elements",
            new ViewElementsData
            {
                View = "Level 1 Plan",
                Processed = 1,
                Elements = { new ViewElementDump { Id = 17 } }
            },
            "Processing interrupted.",
            2500);

        using var json = Parse(response);
        var root = json.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("partial").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("data").GetProperty("processed").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Execute_WhenPreProcessingThrows_WritesFailureResponseFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RevitModelMcp-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "response.json");
        try
        {
            CommandResponseJsonFile.Execute(
                path,
                "failing-command",
                _ => throw new InvalidOperationException("test failure"),
                "job-24");

            await Assert.That(File.Exists(path)).IsTrue();
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();
            await Assert.That(root.GetProperty("partial").GetBoolean()).IsFalse();
            await Assert.That(root.GetProperty("message").GetString()).Contains("test failure");
            await Assert.That(root.GetProperty("correlationId").GetString()).IsEqualTo("job-24");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Serialize_Show_PreservesFalseViewOpenedAndSuppressedDialogs(bool viewOpened)
    {
        var response = CommandResponse<string>.Ok("show", "done", 1);
        response.ActiveView = "Level 5 Plan";
        response.ViewOpened = viewOpened;
        response.DialogsSuppressed = ["Continue?"];
        using var json = Parse(response);
        await Assert.That(json.RootElement.GetProperty("viewOpened").GetBoolean()).IsEqualTo(viewOpened);
        await Assert.That(json.RootElement.GetProperty("activeView").GetString()).IsEqualTo("Level 5 Plan");
        await Assert.That(json.RootElement.GetProperty("dialogsSuppressed")[0].GetString()).IsEqualTo("Continue?");
    }

    [Test]
    public async Task Serialize_ElementDetails_PreservesPointBoundingBoxAndRoomCenter()
    {
        var data = new ElementDetailsData
        {
            Location = new ElementLocationData { Type = "point", XMm = 0, YMm = -123.4, ZMm = 5000 },
            BoundingBox = new ElementBoundingBoxData
            {
                MinMm = [-1000, -500, 5000],
                MaxMm = [1000, 500, 8000],
                CenterMm = [0, 0, 6500]
            },
            RoomCenterMm = [0, -123.4, 5000]
        };
        using var json = Parse(CommandResponse<ElementDetailsData>.Ok("element-details", data, 1));
        var geometry = json.RootElement.GetProperty("data");
        var location = geometry.GetProperty("location");
        await Assert.That(location.GetProperty("type").GetString()).IsEqualTo("point");
        await Assert.That(location.GetProperty("xMm").GetDouble()).IsEqualTo(0);
        await Assert.That(location.GetProperty("yMm").GetDouble()).IsEqualTo(-123.4);
        await Assert.That(location.GetProperty("zMm").GetDouble()).IsEqualTo(5000);
        await Assert.That(location.TryGetProperty("startMm", out _)).IsFalse();
        await Assert.That(location.TryGetProperty("lengthMm", out _)).IsFalse();
        await Assert.That(geometry.GetProperty("roomCenterMm")[1].GetDouble()).IsEqualTo(-123.4);
        var bounds = geometry.GetProperty("boundingBox");
        await Assert.That(bounds.GetProperty("minMm")[0].GetDouble()).IsEqualTo(-1000);
        await Assert.That(bounds.GetProperty("maxMm")[2].GetDouble()).IsEqualTo(8000);
        await Assert.That(bounds.GetProperty("centerMm")[2].GetDouble()).IsEqualTo(6500);
    }

    [Test]
    public async Task Serialize_QueryElements_PreservesCurveGeometryAndOmitsUnavailableFields()
    {
        var data = new QueryElementsData
        {
            Elements =
            [
                new QueryElementItem
                {
                    Id = 17,
                    Location = new ElementLocationData { Type = "curve", StartMm = [0, 10.1, 0], EndMm = [1000.2, 10.1, 0], LengthMm = 1000.2 },
                    BoundingBox = new ElementBoundingBoxData { MinMm = [0, 0, 0], MaxMm = [1000.2, 200, 3000], CenterMm = [500.1, 100, 1500] }
                },
                new QueryElementItem { Id = 18 },
                new QueryElementItem { Id = 19, Location = new ElementLocationData { Type = "point", XMm = 0, YMm = 0, ZMm = 0 }, RoomCenterMm = [0, 0, 0] }
            ]
        };
        using var json = Parse(CommandResponse<QueryElementsData>.Ok("query-elements", data, 1));
        var elements = json.RootElement.GetProperty("data").GetProperty("elements");
        var location = elements[0].GetProperty("location");
        await Assert.That(location.GetProperty("type").GetString()).IsEqualTo("curve");
        await Assert.That(location.GetProperty("startMm")[1].GetDouble()).IsEqualTo(10.1);
        await Assert.That(location.GetProperty("endMm")[0].GetDouble()).IsEqualTo(1000.2);
        await Assert.That(location.GetProperty("lengthMm").GetDouble()).IsEqualTo(1000.2);
        await Assert.That(location.TryGetProperty("xMm", out _)).IsFalse();
        await Assert.That(elements[0].TryGetProperty("roomCenterMm", out _)).IsFalse();
        await Assert.That(elements[0].GetProperty("boundingBox").GetProperty("centerMm")[0].GetDouble()).IsEqualTo(500.1);
        await Assert.That(elements[1].TryGetProperty("location", out _)).IsFalse();
        await Assert.That(elements[1].TryGetProperty("boundingBox", out _)).IsFalse();
        await Assert.That(elements[1].TryGetProperty("roomCenterMm", out _)).IsFalse();
        await Assert.That(elements[2].GetProperty("roomCenterMm")[0].GetDouble()).IsEqualTo(0);

        using var missing = Parse(CommandResponse<ElementDetailsData>.Ok("element-details", new ElementDetailsData(), 1));
        await Assert.That(missing.RootElement.GetProperty("data").TryGetProperty("location", out _)).IsFalse();
        await Assert.That(missing.RootElement.GetProperty("data").TryGetProperty("boundingBox", out _)).IsFalse();
        await Assert.That(missing.RootElement.TryGetProperty("viewOpened", out _)).IsFalse();
    }

    private static JsonDocument Parse<T>(CommandResponse<T> response)
    {
        return JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
    }

    private static async Task AssertSuccess(JsonElement root, string command)
    {
        await Assert.That(root.GetProperty("command").GetString()).IsEqualTo(command);
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("partial").GetBoolean()).IsFalse();
        await Assert.That(root.TryGetProperty("data", out _)).IsTrue();
        await Assert.That(root.TryGetProperty("elapsedMs", out _)).IsTrue();
    }
}
