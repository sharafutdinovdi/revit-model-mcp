using System.Text.Json;
using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Serialization;

public sealed class CommandResponseJsonSerializerTests
{
    [Test]
    public async Task Serialize_VerifiedBatch_RoundTripsFactsAndFalseFlags()
    {
        var verification = new ActionVerification
        {
            Before = new ActionFacts { Id = 1, Parameter = "Comments", Value = "", StorageType = "String", Owner = "instance" },
            After = new ActionFacts { Id = 1, Parameter = "Comments", Value = "Reviewed", StorageType = "String", Owner = "instance" },
            Changed = [1]
        };
        var data = new ActionResultData
        {
            DryRun = false, Committed = false, FailedStep = 1, UndoName = "revit_batch", RolledBack = true,
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
                _ => throw new InvalidOperationException("test failure"));

            await Assert.That(File.Exists(path)).IsTrue();
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();
            await Assert.That(root.GetProperty("partial").GetBoolean()).IsFalse();
            await Assert.That(root.GetProperty("message").GetString()).Contains("test failure");
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
