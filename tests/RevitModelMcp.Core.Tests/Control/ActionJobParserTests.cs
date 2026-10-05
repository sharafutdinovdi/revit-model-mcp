using System.Globalization;
using System.Runtime.Serialization.Json;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ActionJobParserTests
{
    [Test]
    public async Task ActivateView_ParsesZoomModesAndDeduplicatesIds()
    {
        var defaults = ControlJobParser.Parse("""{"command":"activate-view","view":"L1"}""");
        var none = ControlJobParser.Parse("""{"command":"activate-view","view":"L1","zoom":"none"}""");
        var elements = ControlJobParser.Parse("""{"command":"activate-view","view":"L1","zoom":"elements","zoomElementIds":[42,43,42]}""");
        await Assert.That(defaults.Action!.Zoom).IsEqualTo("fit");
        await Assert.That(none.Action!.Zoom).IsEqualTo("none");
        await Assert.That(elements.Action!.ZoomElementIds).IsEquivalentTo([42L, 43L]);
    }

    [Test]
    [Arguments("\"invalid\"")]
    [Arguments("\"elements\"")]
    [Arguments("\"elements\",\"zoomElementIds\":[]")]
    [Arguments("\"elements\",\"zoomElementIds\":[0]")]
    [Arguments("\"elements\",\"zoomElementIds\":[-1]")]
    [Arguments("\"fit\",\"zoomElementIds\":[42]")]
    public async Task ActivateView_RejectsInvalidZoom(string zoom)
    {
        var parsed = ControlJobParser.Parse($$"""{"command":"activate-view","view":"L1","zoom":{{zoom}}}""");
        await Assert.That(parsed.Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task ViewZoomBounds_CombinesCornersAndPadsEachSide()
    {
        var (minimum, maximum) = ViewZoomBounds.Combine([[-10, -20, -5], [10, 20, 5], [30, 60, 15]]);
        await Assert.That(minimum).IsEquivalentTo([-16.0, -32.0, 5.0]);
        await Assert.That(maximum).IsEquivalentTo([36.0, 72.0, 5.0]);
    }

    [Test]
    public async Task ViewZoomBounds_PadsDegenerateRectangleAndRejectsEmptyBounds()
    {
        var (minimum, maximum) = ViewZoomBounds.Combine([[0, 0, 0]]);
        await Assert.That(minimum).IsEquivalentTo([-0.01, -0.01, 0.0]);
        await Assert.That(maximum).IsEquivalentTo([0.01, 0.01, 0.0]);
        await Assert.That(() => ViewZoomBounds.Combine([])).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(2700, true)]
    [Arguments(2700.005, true)]
    [Arguments(2794, false)]
    [Arguments(2888, false)]
    [Arguments(double.NaN, false)]
    [Arguments(double.PositiveInfinity, false)]
    public async Task PlacementOffset_VerifiesElevationFromLevel(double actualMm, bool matches)
    {
        await Assert.That(FamilyPlacementContract.OffsetMatches(2700, actualMm)).IsEqualTo(matches);
    }

    [Test]
    public async Task PlaceFamilies_PreservesLevelOffsetForExplicitAndRoomPlacements()
    {
        var explicitPlacement = ControlJobParser.Parse("""{"command":"place-families","placements":[{"family":"Diffuser","typeName":"A","level":"Level 1","xMm":0,"yMm":0,"zMm":2700}]}""");
        var roomPlacement = ControlJobParser.Parse("""{"command":"place-families","atRooms":{"family":"Diffuser","typeName":"A","level":"Level 1","zMm":2700}}""");
        await Assert.That(explicitPlacement.Action!.Placements![0].ZMm).IsEqualTo(2700);
        await Assert.That(roomPlacement.Action!.AtRooms!.ZMm).IsEqualTo(2700);
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok("place-families",
            new ActionResultData { InstanceOffsetsMm = new Dictionary<string, double?> { ["42"] = 2700, ["43"] = null } }, 1));
        await Assert.That(json).Contains("\"instanceOffsetsMm\":{\"42\":2700,\"43\":null}");
    }

    [Test]
    public async Task OverrideGraphics_ValidatesOptionsAndBatchStep()
    {
        var valid = ControlJobParser.Parse("""{"command":"override-graphics","elementIds":[42],"color":"#12AB34","viewScope":"list","views":["Level 1"],"lineWeight":16,"transparency":100}""");
        await Assert.That(valid.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(valid.Action!.Color).IsEqualTo("#12AB34");
        await Assert.That(valid.Action.Views).IsEquivalentTo(["Level 1"]);
        await Assert.That(ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"override-graphics","elementIds":[42]}]}""").Kind)
            .IsEqualTo(ControlJobKind.Action);
        foreach (var json in new[]
        {
            """{"command":"override-graphics","elementIds":[]}""",
            """{"command":"override-graphics","elementIds":[42],"color":"red"}""",
            """{"command":"override-graphics","elementIds":[42],"lineWeight":17}""",
            """{"command":"override-graphics","elementIds":[42],"transparency":101}""",
            """{"command":"override-graphics","elementIds":[42],"viewScope":"list"}"""
        })
            await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task CreateMepRun_ParsesDirectAndBatchActions()
    {
        const string direct = """{"command":"create-mep-run","kind":"duct","pointsMm":[[0,0],[1000,0],[1000,1000,3000]],"level":"Level 1","widthMm":400,"heightMm":200,"connectTo":42}""";
        var result = ControlJobParser.Parse(direct);
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.PointsMm!.Count).IsEqualTo(3);
        await Assert.That(result.Action.MepHeightMm).IsEqualTo(200);
        await Assert.That(result.Action.ConnectTo).IsEqualTo(42);
        var batch = ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"create-mep-run","kind":"pipe","pointsMm":[[0,0],[1000,0]],"level":"Level 1"}]}""");
        await Assert.That(batch.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(batch.Action!.Steps[0].Command).IsEqualTo("create-mep-run");
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok("create-mep-run", new ActionResultData
        {
            SegmentIds = [11, 12],
            FittingIds = [13],
            UnjoinedPairs = [[11, 12]],
            LengthMm = 2000
        }, 1));
        await Assert.That(json).Contains("\"segmentIds\":[11,12]");
        await Assert.That(json).Contains("\"fittingIds\":[13]");
        await Assert.That(json).Contains("\"unjoinedPairs\":[[11,12]]");
    }

    [Test]
    [Arguments("""{"command":"create-mep-run","kind":"pipe","pointsMm":[[0,0]],"level":"L1"}""")]
    [Arguments("""{"command":"create-mep-run","kind":"pipe","pointsMm":[[0,0],[0,0]],"level":"L1"}""")]
    [Arguments("""{"command":"create-mep-run","kind":"conduit","pointsMm":[[0,0],[100,0]],"level":"L1","widthMm":10}""")]
    [Arguments("""{"command":"create-mep-run","kind":"cable_tray","pointsMm":[[0,0],[100,0]],"level":"L1","diameterMm":10}""")]
    [Arguments("""{"command":"create-mep-run","kind":"duct","pointsMm":[[0,0],[100,0]],"level":"L1","widthMm":10,"diameterMm":10}""")]
    public async Task CreateMepRun_RejectsInvalidInputs(string json)
    {
        await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    [Arguments("pipe", "RBS_PIPE_DIAMETER_PARAM")]
    [Arguments("conduit", "RBS_CONDUIT_DIAMETER_PARAM")]
    [Arguments("duct", "RBS_CURVE_DIAMETER_PARAM")]
    public async Task MepRunSizing_SelectsDiameterParameter(string kind, string expected)
    {
        await Assert.That(MepRunSizing.DiameterParameter(kind)).IsEqualTo(expected);
    }

    [Test]
    public async Task MepRunSizing_ResolvesNominalSizesAndReportsAvailableSizes()
    {
        await Assert.That(MepRunSizing.ResolvePipeDiameter(50, [25, 50.000001, 100])).IsEqualTo(50.000001);
        await Assert.That(() => MepRunSizing.ResolvePipeDiameter(51, [100, 25, 50, 25]))
            .Throws<ArgumentException>().WithMessage("Cannot set pipe diameter 51 mm. Available pipe segment sizes (mm): 25, 50, 100.");
        await Assert.That(() => MepRunSizing.ResolvePipeDiameter(50, []))
            .Throws<ArgumentException>().WithMessage("Cannot set pipe diameter 50 mm. Available pipe segment sizes (mm): none.");
        await Assert.That(() => MepRunSizing.DiameterParameter("cable_tray")).Throws<ArgumentException>();
    }

    [Test]
    public async Task CadActions_ValidateArgumentsAndTrustedPaths()
    {
        var link = ControlJobParser.Parse("""{"command":"link-cad","path":"C:\\Plans\\Floor.dwg","origin":"center","units":"mm","layers":["Walls"]}""");
        await Assert.That(link.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(link.Action!.CadLink).IsTrue();
        await Assert.That(link.Action.Units).IsEqualTo("mm");
        await Assert.That(ControlJobParser.Parse("""{"command":"link-cad","path":"..\\Floor.dwg"}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"link-cad","path":"C:\\Plans\\Floor.rvt"}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"link-cad","path":"\\\\server\\share\\Floor.dwg"}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);

        var walls = ControlJobParser.Parse("""{"command":"walls-from-cad","cadId":5,"layers":["Walls"],"level":"Level 1","minThicknessMm":100,"maxThicknessMm":400,"dryRun":true}""");
        await Assert.That(walls.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(walls.Action!.DryRun).IsTrue();
        await Assert.That(walls.Action.HeightMm).IsEqualTo(3000);
        await Assert.That(ControlJobParser.Parse("""{"command":"walls-from-cad","cadId":5,"layers":["Walls"],"level":"L1","minThicknessMm":500,"maxThicknessMm":100}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"walls-from-cad","cadId":5,"layers":["Walls"],"level":"L1"}]}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task CadWallPlanner_BridgesDoorAndJoinsRectangle()
    {
        var result = CadWallPlanner.Build([
            Segment(0, 0, 2000, 0), Segment(3000, 0, 5000, 0),
            Segment(0, 200, 2000, 200), Segment(3000, 200, 5000, 200),
            Segment(0, 4000, 5000, 4000), Segment(0, 3800, 5000, 3800),
            Segment(0, 0, 0, 4000), Segment(200, 0, 200, 4000),
            Segment(5000, 0, 5000, 4000), Segment(4800, 0, 4800, 4000)
        ], 80, 700, 300, 3000, true);
        await Assert.That(result.Walls.Count).IsEqualTo(4);
        await Assert.That(result.MergedSegments).IsEqualTo(2);
        await Assert.That(result.UnpairedLines).IsEqualTo(0);
        foreach (var wall in result.Walls)
            foreach (var endpoint in new[] { wall.Start, wall.End })
                await Assert.That(result.Walls.Where(other => other != wall).Any(other =>
                    (other.Start - endpoint).Length < 1e-6 || (other.End - endpoint).Length < 1e-6)).IsTrue();
    }

    [Test]
    public async Task CadWallPlanner_TJunctionMeetsTrunk()
    {
        var result = CadWallPlanner.Build([
            Segment(0, 0, 5000, 0), Segment(0, 200, 5000, 200),
            Segment(2400, 200, 2400, 3000), Segment(2600, 200, 2600, 3000)
        ], 80, 700, 300, 3000, true);
        await Assert.That(result.Walls.Count).IsEqualTo(2);
        var branch = result.Walls.Single(wall => Math.Abs(wall.Start.X - wall.End.X) < 1e-6);
        await Assert.That(branch.Start).IsEqualTo(new CadPlanPoint(2500, 100));
    }

    [Test]
    public async Task CadWallPlanner_ReusesLineWithDifferentPartners()
    {
        var result = CadWallPlanner.Build([
            Segment(0, 0, 5000, 0), Segment(0, 200, 2000, 200), Segment(2000, 300, 5000, 300)
        ], 80, 700, 300, 100, false);
        await Assert.That(result.Walls.Count).IsEqualTo(2);
        await Assert.That(result.UnpairedLines).IsEqualTo(0);
        await Assert.That(result.Walls.Sum(wall => (wall.End - wall.Start).Length)).IsEqualTo(5000);
    }

    [Test]
    public async Task CadWallPlanner_MergesShortPiecesBeforeFilteringAndHonorsGap()
    {
        var segments = new[] { Segment(0, 0, 100, 0), Segment(200, 0, 1000, 0), Segment(0, 200, 1000, 200) };
        var bridged = CadWallPlanner.Build(segments, 80, 700, 300, 100, false);
        await Assert.That(bridged.MergedSegments).IsEqualTo(1);
        await Assert.That((bridged.Walls[0].End - bridged.Walls[0].Start).Length).IsEqualTo(1000);
        var separated = CadWallPlanner.Build(segments, 80, 700, 300, 99, false);
        await Assert.That(separated.MergedSegments).IsEqualTo(0);
        await Assert.That(separated.SkippedShortSegments).IsEqualTo(1);
        await Assert.That((separated.Walls[0].End - separated.Walls[0].Start).Length).IsEqualTo(800);
        await Assert.That(ControlJobParser.Parse("""{"command":"walls-from-cad","cadId":5,"layers":["Walls"],"level":"L1","maxGapMm":-1}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    private static CadPlanSegment Segment(double startX, double startY, double endX, double endY) =>
        new(new CadPlanPoint(startX, startY), new CadPlanPoint(endX, endY), "Walls");

    [Test]
    public async Task SelectWritableInPlacePaths_SeparatesReadOnlySources()
    {
        var (writable, refused) = ProcessModelsJob.SelectWritableInPlacePaths(
            ["A.rvt", "B.rvt", "C.rvt"], path => path == "B.rvt");

        await Assert.That(writable).IsEquivalentTo(["A.rvt", "C.rvt"]);
        await Assert.That(refused).IsEquivalentTo(["B.rvt"]);
        var (none, allRefused) = ProcessModelsJob.SelectWritableInPlacePaths(
            ["B.rvt"], path => path == "B.rvt");
        await Assert.That(none).IsEmpty();
        await Assert.That(allRefused).IsEquivalentTo(["B.rvt"]);
    }

    [Test]
    public async Task ProcessModels_ParsesNestedActionsAndRejectsInvalidSources()
    {
        var valid = ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["C:\\Models\\A.rvt"],"steps":[{"command":"set-parameter","elementId":1,"parameter":"Mark","value":"done"}],"code":{"code":"return 1;","transaction":"auto"},"exports":[{"format":"ifc"}],"save":{"mode":"output_dir","outputDir":"C:\\Out"}}}""");
        await Assert.That(valid.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(valid.Action!.ProcessModels!.Steps!.Count).IsEqualTo(1);
        await Assert.That(valid.Action.ProcessModels.Exports!.Count).IsEqualTo(1);
        using (var stream = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(ProcessModelsJob)).WriteObject(stream, valid.Action.ProcessModels);
            await Assert.That(stream.Length).IsGreaterThan(0);
        }
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["relative.rvt"]}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["C:\\A.rvt"],"folder":"C:\\Models"}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["C:\\A.rvt"],"steps":[{"command":"export","format":"ifc"}]}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["C:\\A.rvt"],"code":{"code":"return 1;","transaction":"none"},"dryRun":true}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task ProcessModels_ValidatesSaveModesAndSerializesScriptResults()
    {
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["C:\\A.rvt"],"save":{"mode":"output_dir"}}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["C:\\A.rvt"],"save":{"mode":"output_dir","outputDir":"C:\\"}}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["C:\\A.rvt"],"save":{"mode":"in_place","outputDir":"C:\\Out"}}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["C:\\A.rvt"],"open":{"mode":"local_copy"},"save":{"mode":"in_place"}}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"process-models","process":{"paths":["RSN://host/folder/A.rvt"],"save":{"mode":"in_place"}}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(() => new ProcessSaveJob { Mode = "in_place" }.EnsureInPlaceAllowed(true))
            .Throws<InvalidOperationException>();
        new ProcessSaveJob { Mode = "in_place" }.EnsureInPlaceAllowed(false);

        var data = new ActionResultData
        {
            Models = [new ProcessModelResult
            {
                Path = "C:\\A.rvt", Status = "done", Code = new ProcessModelCodeResult
                {
                    ReturnValue = 42, ReturnValueMarker = "test-marker", Log = ["counted"]
                }
            }]
        };
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok("process-models", data, 1));
        await Assert.That(json.Contains("\"returnValue\":42")).IsTrue();
        await Assert.That(json.Contains("test-marker")).IsFalse();
    }

    [Test]
    public async Task ExecuteCode_ValidatesModeSizeAndBatchExclusion()
    {
        var parsed = ControlJobParser.Parse("""{"command":"execute-code","code":"return 42;"}""");
        await Assert.That(parsed.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(parsed.Action!.Code).IsEqualTo("return 42;");
        await Assert.That(parsed.Action.TransactionMode).IsEqualTo("auto");
        await Assert.That(ControlJobParser.Parse("""{"command":"execute-code","code":"x","transaction":"none","dryRun":true}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"execute-code","code":""}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"execute-code","code":"return 42;"}]}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task ExecuteCode_SourceAndCacheKeyAreStable()
    {
        var body = CodeSource.BuildSource("return ctx.Document?.Title;");
        var full = CodeSource.BuildSource("public static class Script { public static object Execute(ScriptContext ctx) => 1; }");
        await Assert.That(CodeSource.IsCompilationUnit("return 1;")).IsFalse();
        await Assert.That(CodeSource.IsCompilationUnit(full)).IsTrue();
        await Assert.That(body.Contains("#line 1 \"submitted.cs\"\nreturn ctx.Document?.Title;")).IsTrue();
        await Assert.That(full.Contains("#line 1 \"submitted.cs\"\npublic static class Script")).IsTrue();
        await Assert.That(CodeSource.CacheKey("return 1;", "auto"))
            .IsNotEqualTo(CodeSource.CacheKey("return 1;", "none"));
        await Assert.That(CodeSource.CacheKey("return 1;", "auto"))
            .IsEqualTo(CodeSource.CacheKey("return 1;", "auto"));
        await Assert.That(CodeSource.FirstLine("\n  return 1;\n")).IsEqualTo("return 1;");
    }

    [Test]
    public async Task ExecuteCode_ReturnLimiterCapsDepthAndItems()
    {
        var items = CodeResultLimiter.Limit(Enumerable.Range(0, 6000).ToArray(), value => value) as List<object?>;
        object nested = 7;
        for (var depth = 0; depth < 8; depth++) nested = new[] { nested };
        var limited = CodeResultLimiter.Limit(nested, value => value);
        await Assert.That(items!.Count).IsEqualTo(5000);
        await Assert.That(CodeResultLimiter.ToJson(limited).Contains("System.Object[]")).IsTrue();
        await Assert.That(CodeResultLimiter.ToJson(new Dictionary<string, object?>
        {
            ["escaped"] = "a\"b\n",
            ["number"] = 1.5,
            ["finite"] = double.NaN
        })).IsEqualTo("{\"escaped\":\"a\\\"b\\n\",\"number\":1.5,\"finite\":null}");
    }

    [Test]
    public async Task ExecuteCode_ReturnLimiterSerializesPlainPropertiesWithInvariantNumbers()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var anonymous = CodeResultLimiter.Limit(new { Name = "Parking", mm = -5156.2 }, value => value);
            var plain = CodeResultLimiter.Limit(new PlainCodeResult { Name = "Parking", Millimeters = -5156.2 }, value => value);
            await Assert.That(CodeResultLimiter.ToJson(anonymous))
                .IsEqualTo("{\"Name\":\"Parking\",\"mm\":-5156.2}");
            await Assert.That(CodeResultLimiter.ToJson(plain))
                .IsEqualTo("{\"Name\":\"Parking\",\"Millimeters\":-5156.2}");
            await Assert.That(CodeResultLimiter.ToJson(CodeResultLimiter.Limit(
                new Dictionary<double, object?> { [-5156.2] = 1.5 }, value => value)))
                .IsEqualTo("{\"-5156.2\":1.5}");
            await Assert.That(CodeResultLimiter.ToJson(CodeResultLimiter.Limit(
                new Uri("https://example.org/"), value => value)))
                .IsEqualTo("\"https://example.org/\"");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private sealed class PlainCodeResult
    {
        public string Name { get; init; } = string.Empty;
        public double Millimeters { get; init; }
    }

    [Test]
    [Arguments("pdf", "A-01_Site.pdf")]
    [Arguments("dwg", "A-01_Site.dwg")]
    public async Task Export_FileNamingUsesSheetNumber(string format, string expected)
    {
        var name = FileExportJob.FileName("A-01_Site", format);
        await Assert.That(name).IsEqualTo(expected);
        await Assert.That(FileExportJob.FileName("CON", format)).IsEqualTo($"_CON.{format}");
        await Assert.That(FileExportJob.FileName("A/01", format)).IsEqualTo($"A_01.{format}");
    }

    [Test]
    [Arguments("COM2")]
    [Arguments("COM9.report")]
    [Arguments("LPT5")]
    [Arguments("LPT9.report")]
    public async Task Export_FileNamingPrefixesReservedDeviceNames(string name)
    {
        await Assert.That(FileExportJob.FileName(name, "csv")).IsEqualTo($"_{name}.csv");
    }

    [Test]
    public async Task ScheduleData_SerializesNamedResult()
    {
        var data = new ScheduleDataResult
        {
            Columns = ["Door number"],
            Rows = [["101"]],
            TotalRows = 2,
            Truncated = true
        };
        using var json = System.Text.Json.JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<ScheduleDataResult>.Ok("schedule-data", data, 1)));
        var result = json.RootElement.GetProperty("data");
        await Assert.That(result.GetProperty("columns")[0].GetString()).IsEqualTo("Door number");
        await Assert.That(result.GetProperty("rows")[0][0].GetString()).IsEqualTo("101");
        await Assert.That(result.GetProperty("totalRows").GetInt32()).IsEqualTo(2);
        await Assert.That(result.GetProperty("truncated").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ScheduleData_JoinsGroupedHeadingsByColumn()
    {
        List<List<string>> rows =
        [
            ["Door", "Door", "Door"],
            ["Mark", "Size", "Size"],
            ["", "Width", "Height"]
        ];
        var columns = ScheduleDataResult.JoinHeadings(rows, 3);
        await Assert.That(columns[0]).IsEqualTo("Door / Mark");
        await Assert.That(columns[1]).IsEqualTo("Door / Size / Width");
        await Assert.That(columns[2]).IsEqualTo("Door / Size / Height");
    }

    [Test]
    [Arguments("""{"command":"export","format":"pdf"}""")]
    [Arguments("""{"command":"export","format":"csv","sheets":["A1"]}""")]
    [Arguments("""{"command":"export","format":"ifc","views":["One","Two"]}""")]
    [Arguments("""{"command":"export","format":"pdf","views":["A"],"options":{"zoom_percent":0}}""")]
    [Arguments("""{"command":"export","format":"csv","options":{"encoding":"latin-1"}}""")]
    [Arguments("""{"command":"batch","steps":[{"command":"export","format":"ifc"}]}""")]
    public async Task Export_RejectsInvalidRequests(string json)
    {
        await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task Export_ParsesOptionsAndTargets()
    {
        var result = ControlJobParser.Parse("""{"command":"export","format":"pdf","sheets":["A1"],"folder":"C:\\out","options":{"combine":false,"color":"grayscale"}}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.Export.Sheets).IsEquivalentTo(new[] { "A1" });
        await Assert.That(result.Action.Export.Options.Combine).IsFalse();
        await Assert.That(result.Action.Export.Options.Color).IsEqualTo("grayscale");
    }

    [Test]
    public async Task ScheduleData_ParsesPagingAndRejectsInvalidLimits()
    {
        var result = ControlJobParser.Parse("""{"command":"schedule-data","view":"Doors","limit":25,"offset":10}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ScheduleData);
        await Assert.That(result.Limit).IsEqualTo(25);
        await Assert.That(result.Offset).IsEqualTo(10);
        await Assert.That(ControlJobParser.Parse("""{"command":"schedule-data","view":"Doors","limit":0}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task DocumentIdentityMatcher_MatchesWrappersButDistinguishesCopies()
    {
        var original = new DocumentIdentity(Guid.NewGuid(), "Night test project", "C:/Models/Night test project.rvt");
        var wrapper = new DocumentIdentity(original.Id, original.Title, original.Path);
        var copy = new DocumentIdentity(Guid.NewGuid(), original.Title, original.Path);
        var structural = new DocumentIdentity(Guid.NewGuid(), "Structural", "C:/Models/Structural.rvt");
        var before = new[] { structural };
        var comparer = new DocumentIdentityComparer<DocumentIdentity>((first, second) => first.Equals(second));
        var opened = new Dictionary<DocumentIdentity, bool>(comparer)
        {
            [original] = true
        };

        await Assert.That(DocumentIdentityMatcher.Contains(new[] { original, structural }, wrapper, comparer)).IsTrue();
        await Assert.That(DocumentIdentityMatcher.Contains(new[] { original, structural }, copy, comparer)).IsFalse();
        await Assert.That(opened.ContainsKey(wrapper)).IsTrue();
        await Assert.That(opened.ContainsKey(copy)).IsFalse();
        await Assert.That(DocumentIdentityMatcher.AllPresent(new[] { original }, new[] { wrapper }, comparer)).IsTrue();
        await Assert.That(DocumentIdentityMatcher.AllPresent(before, new[] { original, copy }, comparer)).IsFalse();
        await Assert.That(DocumentIdentityMatcher.AllPresent(before, new[] { structural, copy }, comparer)).IsTrue();
        await Assert.That(opened.Remove(wrapper)).IsTrue();
    }

    private sealed class DocumentIdentity(Guid id, string title, string path)
    {
        public Guid Id { get; } = id;
        public string Title { get; } = title;
        public string Path { get; } = path;

        public bool Equals(DocumentIdentity? other) => other is not null && Id == other.Id;
    }

    [Test]
    public async Task OpenWorksetSelector_MatchesWildcardsAndReportsUnmatchedPatterns()
    {
        var available = new[] { "Architecture", "Shared Levels and Grids", "Furniture", "Model Links" };
        var open = OpenWorksetSelector.Select(available, "open", ["Arch*", "Shared Levels and Grids", "Missing?"]);
        await Assert.That(open.Selected).IsEquivalentTo(new[] { "Architecture", "Shared Levels and Grids" });
        await Assert.That(open.Unmatched).IsEquivalentTo(new[] { "Missing?" });
        var close = OpenWorksetSelector.Select(available, "close", ["*link*", "*Furniture*"]);
        await Assert.That(close.Selected).IsEquivalentTo(new[] { "Architecture", "Shared Levels and Grids" });
        await Assert.That(() => OpenWorksetSelector.Select(available, "open", ["Missing"]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task SessionActions_ValidateArgumentsAndStayOutOfBatch()
    {
        await Assert.That(ControlJobParser.Parse("""{"command":"activate-document"}""").Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"activate-view"}""").Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"new-document","kind":"family"}""").Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"activate-document","document":"A"}]}""").Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"open-document","path":"C:\\a.rte","audit":true,"worksets":"close","worksetsClose":["*Link*"]}""").Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(ControlJobParser.Parse("""{"command":"new-document","kind":"family","template":"C:\\a.rft"}""").Kind).IsEqualTo(ControlJobKind.Action);
    }

    [Test]
    [Arguments("rotate", "\"elementIds\":[1],\"angleDeg\":90")]
    [Arguments("copy", "\"elementIds\":[1],\"dxMm\":100,\"dyMm\":0,\"count\":2")]
    [Arguments("mirror", "\"elementIds\":[1],\"axis\":\"x\",\"pointMm\":[0,0]")]
    [Arguments("change-type", "\"elementIds\":[1],\"typeName\":\"Basic\"")]
    [Arguments("update-parameters", "\"queryFilters\":{\"categories\":[\"Walls\"],\"level\":\"Level 1\"},\"parameter\":\"Mark\",\"value\":\"A\"")]
    public async Task NewActions_ParseDirectAndBatch(string command, string arguments)
    {
        var step = $"{{\"command\":\"{command}\",{arguments}}}";
        await Assert.That(ControlJobParser.Parse(step).Kind).IsEqualTo(ControlJobKind.Action);
        var batch = ControlJobParser.Parse($"{{\"command\":\"batch\",\"steps\":[{step}]}}");
        await Assert.That(batch.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(batch.Action!.Steps[0].Command).IsEqualTo(command);
        if (command == "update-parameters")
        {
            await Assert.That(batch.Action.Steps[0].Action!.QueryFilters!.Categories[0]).IsEqualTo("Walls");
            await Assert.That(batch.Action.Steps[0].Action!.QueryFilters!.Level).IsEqualTo("Level 1");
        }
    }

    [Test]
    [Arguments("rotate", "\"elementIds\":[1],\"angleDeg\":1e999")]
    [Arguments("copy", "\"elementIds\":[1],\"dxMm\":1,\"dyMm\":0,\"count\":101")]
    [Arguments("mirror", "\"elementIds\":[1],\"axis\":\"z\",\"pointMm\":[0,0]")]
    [Arguments("change-type", "\"elementIds\":[1],\"typeName\":\"\"")]
    [Arguments("update-parameters", "\"queryFilters\":{},\"parameter\":\"Mark\",\"value\":\"A\",\"maxElements\":20001")]
    public async Task NewActions_RejectInvalidArguments(string command, string arguments)
    {
        await Assert.That(ControlJobParser.Parse($"{{\"command\":\"{command}\",{arguments}}}").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    [Arguments(false, true, "loaded")]
    [Arguments(true, true, "reloaded")]
    [Arguments(true, false, "unchanged")]
    public async Task FamilyLoadResult_ReportsLoadStatus(bool wasLoaded, bool loadSucceeded, string expectedStatus)
    {
        await Assert.That(FamilyLoadResult.StatusFor("Chair", wasLoaded, loadSucceeded)).IsEqualTo(expectedStatus);
    }

    [Test]
    public async Task FamilyLoadResult_RejectsFailedFirstLoad()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(FamilyLoadResult.StatusFor("Chair", false, false)));
    }

    [Test]
    public async Task ActionResults_SerializeSharedFieldsForEachAction()
    {
        var process = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok(
            "process-models", new ActionResultData { Failed = 1 }, 1));
        var parameters = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok(
            "update-parameters", new ActionResultData
            {
                Skipped = new Dictionary<string, List<long>> { ["missing"] = [1] }
            }, 1));
        var families = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok(
            "place-families", new ActionResultData
            {
                Failed = new List<PlacementFailure> { new() { Index = 1, Reason = "Unavailable" } },
                Skipped = new List<PlacementFailure> { new() { Index = 2, Reason = "No room" } }
            }, 1));
        await Assert.That(process.Contains("\"failed\":1")).IsTrue();
        await Assert.That(parameters.Contains("\"key\":\"missing\"")).IsTrue();
        await Assert.That(families.Contains("\"failed\":[")).IsTrue();
        await Assert.That(families.Contains("\"skipped\":[")).IsTrue();
    }

    [Test]
    public async Task BulkFamilyJobs_ValidatePathsAndPlacementLimits()
    {
        var load = ControlJobParser.Parse("""{"command":"load-family","paths":["C:\\Families\\Chair.rfa"]}""");
        await Assert.That(load.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(load.Action!.Paths!.Count).IsEqualTo(1);
        await Assert.That(ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"load-family","paths":["C:\\Families\\Chair.rfa"]}]}""").Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(ControlJobParser.Parse("""{"command":"load-family","paths":["C:\\Families\\Chair.rvt"]}""").Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ActionJobParser.Parse("load-family", new ControlJobContract { Paths = Enumerable.Repeat(@"C:\Families\Chair.rfa", 101).ToList() }).Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"place-families","placements":[],"atRooms":{"family":"Chair","typeName":"A"}}""").Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"place-families","placements":[{"family":"Chair","typeName":"A","level":"L1","xMm":0,"yMm":0}]}""").Kind).IsEqualTo(ControlJobKind.Action);
        var withParameters = ControlJobParser.Parse("""{"command":"place-families","placements":[{"family":"Chair","typeName":"A","level":"L1","xMm":0,"yMm":0,"parameters":{"Mark":"C1"}}]}""");
        await Assert.That(withParameters.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(withParameters.Action!.Placements![0].Parameters!["Mark"]).IsEqualTo("C1");
        await Assert.That(ControlJobParser.Parse("""{"command":"place-families","atRooms":{"family":"Chair","typeName":"A","rooms":["101"]}}""").Kind).IsEqualTo(ControlJobKind.Action);
        var tooMany = new ControlJobContract
        {
            Placements = Enumerable.Range(0, 2001).Select(_ => new FamilyPlacementContract { Family = "Chair", TypeName = "A", Level = "L1" }).ToList()
        };
        await Assert.That(ActionJobParser.Parse("place-families", tooMany).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task SetParameter_PreservesIdentifierAndTypedValueInDirectAndBatchJobs()
    {
        var direct = ControlJobParser.Parse("""{"command":"set-parameter","elementId":1,"parameter":"Mark","parameterId":"ALL_MODEL_MARK","value":42}""");
        var batch = ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"set-parameter","elementId":1,"parameter":"Mark","parameterId":"123","value":3.5}]}""");
        await Assert.That(direct.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(direct.Action!.ParameterId).IsEqualTo("ALL_MODEL_MARK");
        await Assert.That(direct.Action.Value).IsEqualTo(42);
        await Assert.That(batch.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(batch.Action!.Steps[0].Action!.ParameterId).IsEqualTo("123");
        await Assert.That(Convert.ToDouble(batch.Action.Steps[0].Action!.Value)).IsEqualTo(3.5);
        var shared = ControlJobParser.Parse("""{"command":"set-parameter","elementId":1,"parameter":"Code","parameterId":"f5257291-6b0b-4ef4-a9a1-b5aa937127a4","value":"ok"}""");
        await Assert.That(shared.Action!.ParameterId).IsEqualTo("f5257291-6b0b-4ef4-a9a1-b5aa937127a4");
    }

    [Test]
    public async Task UpdateParameters_TypeParametersRequireExplicitOptInInDirectAndBatchJobs()
    {
        var direct = ControlJobParser.Parse("""{"command":"update-parameters","queryFilters":{},"parameter":"Mark","value":"A"}""");
        var batch = ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"update-parameters","queryFilters":{},"parameter":"Mark","value":"A","includeTypeParameters":true}]}""");
        await Assert.That(direct.Action!.IncludeTypeParameters).IsFalse();
        await Assert.That(batch.Action!.Steps[0].Action!.IncludeTypeParameters).IsTrue();
    }

    [Test]
    [Arguments(" ", "\"x\"")]
    [Arguments("-1", "\"x\"")]
    [Arguments("123", "true")]
    public async Task SetParameter_RejectsInvalidIdentifierOrValue(string identifier, string value)
    {
        var json = $"{{\"command\":\"set-parameter\",\"elementId\":1,\"parameter\":\"Mark\",\"parameterId\":\"{identifier}\",\"value\":{value}}}";
        await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
        var batch = $"{{\"command\":\"batch\",\"steps\":[{json}]}}";
        await Assert.That(ControlJobParser.Parse(batch).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task DocumentConfirmationTokens_AreSingleUseBoundAndExpire()
    {
        var now = DateTimeOffset.UtcNow;
        var tokens = new DocumentConfirmationTokens(() => now);
        var first = tokens.Issue("save-document", "document-1", "path=A", "state-1");
        await Assert.That(tokens.Consume(first, "save-document", "document-1", "path=B", "state-1")).IsEqualTo(DocumentConfirmationResult.ArgumentsMismatch);
        await Assert.That(tokens.Consume(first, "save-document", "document-1", "path=A", "state-1")).IsEqualTo(DocumentConfirmationResult.Invalid);
        var second = tokens.Issue("save-document", "document-1", "path=A", "state-1");
        await Assert.That(tokens.Consume(second, "save-document", "document-1", "path=A", "state-1")).IsEqualTo(DocumentConfirmationResult.Valid);
        await Assert.That(tokens.Consume(second, "save-document", "document-1", "path=A", "state-1")).IsEqualTo(DocumentConfirmationResult.Invalid);
        var third = tokens.Issue("save-document", "document-1", "path=A", "state-1");
        now = now.AddMinutes(5);
        await Assert.That(tokens.Consume(third, "save-document", "document-1", "path=A", "state-1")).IsEqualTo(DocumentConfirmationResult.Invalid);
    }

    [Test]
    [Arguments("document-1", "path=A", "state-2", DocumentConfirmationResult.DocumentChanged)]
    [Arguments("document-1", "path=B", "state-2", DocumentConfirmationResult.ArgumentsMismatch)]
    [Arguments("document-2", "path=A", "state-1", DocumentConfirmationResult.ArgumentsMismatch)]
    public async Task DocumentConfirmationTokens_ClassifyMismatchAndBurnToken(
        string document, string arguments, string state, DocumentConfirmationResult expected)
    {
        var tokens = new DocumentConfirmationTokens();
        var token = tokens.Issue("save-document", "document-1", "path=A", "state-1");
        await Assert.That(tokens.Consume(token, "save-document", document, arguments, state)).IsEqualTo(expected);
        await Assert.That(tokens.Consume(token, "save-document", "document-1", "path=A", "state-1")).IsEqualTo(DocumentConfirmationResult.Invalid);
    }

    [Test]
    public async Task DocumentConfirmationTokens_IssuePrunesExpiredTokens()
    {
        var now = DateTimeOffset.UtcNow;
        var tokens = new DocumentConfirmationTokens(() => now);
        var expired = tokens.Issue("save-document", "document-1", "path=A", "state-1");
        now = now.AddMinutes(5);
        var current = tokens.Issue("save-document", "document-1", "path=A", "state-1");
        now = now.AddMinutes(-5);

        await Assert.That(tokens.Consume(expired, "save-document", "document-1", "path=A", "state-1"))
            .IsEqualTo(DocumentConfirmationResult.Invalid);
        await Assert.That(tokens.Consume(current, "save-document", "document-1", "path=A", "state-1"))
            .IsEqualTo(DocumentConfirmationResult.Valid);
    }

    [Test]
    public async Task DocumentConfirmationTokens_IssueEvictsOldestBeyondOneHundred()
    {
        var tokens = new DocumentConfirmationTokens();
        var issued = Enumerable.Range(0, 101)
            .Select(_ => tokens.Issue("save-document", "document-1", "path=A", "state-1"))
            .ToArray();

        await Assert.That(tokens.Consume(issued[0], "save-document", "document-1", "path=A", "state-1"))
            .IsEqualTo(DocumentConfirmationResult.Invalid);
        await Assert.That(tokens.Consume(issued[1], "save-document", "document-1", "path=A", "state-1"))
            .IsEqualTo(DocumentConfirmationResult.Valid);
        await Assert.That(tokens.Consume(issued[100], "save-document", "document-1", "path=A", "state-1"))
            .IsEqualTo(DocumentConfirmationResult.Valid);
    }

    [Test]
    public async Task DocumentConfirmationBinding_IssueThenConfirm_SucceedsDespiteUnrelatedFieldChanges()
    {
        var tokens = new DocumentConfirmationTokens();
        var issueAction = new ActionJobContract
        {
            Document = "Tower",
            Save = true,
            Comment = "grids",
            ElementIds = [1, 2, 3],
            DryRun = false
        };
        var identity = DocumentConfirmationBinding.Identity(@"C:\Models\Tower.rvt", "Tower.rvt");
        var arguments = DocumentConfirmationBinding.Arguments(issueAction, @"C:\Models\Tower.rvt", isModified: true);
        var state = DocumentConfirmationBinding.State(Guid.NewGuid(), 3, Guid.NewGuid(), 7);
        var token = tokens.Issue("save-document", identity, arguments, state);

        // The confirming call carries fields that never enter the fingerprint (a fresh element
        // selection here stands in for the request's transport metadata, which lives entirely
        // outside ActionJobContract) plus the confirm_token itself, but the same business arguments.
        var confirmAction = new ActionJobContract
        {
            Document = "Tower",
            Save = true,
            Comment = "grids",
            ElementIds = [9, 8],
            DryRun = false,
            ConfirmToken = token
        };
        var confirmIdentity = DocumentConfirmationBinding.Identity(@"C:\Models\Tower.rvt", "Tower.rvt");
        var confirmArguments = DocumentConfirmationBinding.Arguments(confirmAction, @"C:\Models\Tower.rvt", isModified: true);

        await Assert.That(tokens.Consume(token, "save-document", confirmIdentity, confirmArguments, state)).IsEqualTo(DocumentConfirmationResult.Valid);
    }

    [Test]
    public async Task DocumentConfirmationBinding_StateChangeRejectsConfirmation()
    {
        var versionGuid = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var initial = DocumentConfirmationBinding.State(versionGuid, 3, sessionId, 7);
        var changedVersion = DocumentConfirmationBinding.State(Guid.NewGuid(), 3, sessionId, 7);
        var changedSaves = DocumentConfirmationBinding.State(versionGuid, 4, sessionId, 7);
        var changedSession = DocumentConfirmationBinding.State(versionGuid, 3, Guid.NewGuid(), 7);
        var changedCount = DocumentConfirmationBinding.State(versionGuid, 3, sessionId, 8);
        var tokens = new DocumentConfirmationTokens();

        await Assert.That(initial).IsNotEqualTo(changedVersion);
        await Assert.That(initial).IsNotEqualTo(changedSaves);
        await Assert.That(initial).IsNotEqualTo(changedSession);
        await Assert.That(initial).IsNotEqualTo(changedCount);
        foreach (var changed in new[] { changedVersion, changedSaves, changedSession, changedCount })
        {
            var token = tokens.Issue("save-document", "document-1", "path=A", initial);
            await Assert.That(tokens.Consume(token, "save-document", "document-1", "path=A", changed))
                .IsEqualTo(DocumentConfirmationResult.DocumentChanged);
        }
    }

    [Test]
    public async Task DocumentConfirmationBinding_Identity_NormalizesPathCaseAndTrailingSeparators()
    {
        var first = DocumentConfirmationBinding.Identity(@"C:\Models\Tower.rvt\", "Tower.rvt");
        var second = DocumentConfirmationBinding.Identity(@"c:\models\tower.rvt", "Tower.rvt");
        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task DocumentConfirmationBinding_Arguments_ChangesWhenBusinessFieldChanges()
    {
        var action = new ActionJobContract { Document = "Tower", Save = true };
        var baseline = DocumentConfirmationBinding.Arguments(action, @"C:\Models\Tower.rvt", isModified: true);
        action.Save = false;
        var changed = DocumentConfirmationBinding.Arguments(action, @"C:\Models\Tower.rvt", isModified: true);
        await Assert.That(baseline).IsNotEqualTo(changed);
    }

    [Test]
    public async Task DocumentPaths_RejectCloudAndMalformedServerPaths()
    {
        DocumentPathValidator.Validate("RSN://server/folder/model.rvt");
        DocumentPathValidator.Validate(@"C:\models\file.rvt");
        DocumentPathValidator.Validate(@"C:\models\family.rfa");
        foreach (var path in new[] { "RSN://server/model.rvt", "RSN://server//model.rvt", "BIM360://hub/model.rvt", "relative.rvt" })
            await Assert.That(() => DocumentPathValidator.Validate(path)).Throws<ArgumentException>();
    }

    [Test]
    public async Task DocumentActions_ValidateModesWorksetsAndSyncComment()
    {
        await Assert.That(ControlJobParser.Parse("""{"command":"open-document","path":"RSN://server/folder/model.rvt"}""").Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(ControlJobParser.Parse("""{"command":"open-document","path":"C:\\x\\a.rvt","worksets":"open","worksetsOpen":["A"]}""").Kind).IsEqualTo(ControlJobKind.Action);
        var sync = ControlJobParser.Parse("""{"command":"sync-document","document":"A","comment":"grids","relinquish":"custom","relinquishFlags":{"borrowed":true}}""");
        await Assert.That(sync.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(sync.Action!.RelinquishFlags!["borrowed"]).IsTrue();
        foreach (var json in new[]
        {
            """{"command":"open-document","path":"C:\\x\\a.rvt","mode":"central"}""",
            """{"command":"open-document","path":"C:\\x\\a.rvt","worksets":"bad"}""",
            """{"command":"sync-document","document":"A"}""",
            """{"command":"save-document","document":"C:\\x\\a.rvt","saveAs":"C:/x/../a.rvt"}"""
        })
            await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"save-document","document":"A"}]}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task SessionActions_ParseViewTypeAndSafeDocumentName()
    {
        var view = ControlJobParser.Parse("""{"command":"activate-view","view":"L2","viewType":"FloorPlan"}""");
        await Assert.That(view.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(view.Action!.ViewType).IsEqualTo("FloorPlan");
        var document = ControlJobParser.Parse("""{"command":"new-document","name":"Project review"}""");
        await Assert.That(document.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(document.Action!.NewDocumentName).IsEqualTo("Project review");
        foreach (var name in new[] { "", "..", "CON", "CON.txt", "A/B", "A\\B", "A: B", "A. ", " name" })
        {
            var invalid = ControlJobParser.Parse($$"""{"command":"new-document","name":{{System.Text.Json.JsonSerializer.Serialize(name)}}}""");
            await Assert.That(invalid.Kind).IsEqualTo(ControlJobKind.Invalid);
        }
    }

    [Test]
    public async Task DocumentPaths_RejectTraversalAndDeviceAliases()
    {
        foreach (var path in new[] { @"C:\x\..\a.rvt", "C:/x/../a.rvt", @"\\?\UNC\srv\share\a.rvt", @"\\.\C:\x\a.rvt" })
            await Assert.That(() => DocumentPathValidator.Validate(path)).Throws<ArgumentException>();
    }

    [Test]
    public async Task DocumentPaths_CompareNormalizedCentralAndGuardExistingWorksharedFile()
    {
        await Assert.That(DocumentPathValidator.SamePath("C:/x/y.rvt", @"C:\X\Y.RVT")).IsTrue();
        await Assert.That(DocumentPathValidator.SamePath("C:/x/y.rvt", @"C:\X\other.rvt")).IsFalse();
        await Assert.That(() => DocumentPathValidator.EnsureSaveAsDiffersFromCentral("C:/x/y.rvt", @"C:\X\Y.RVT"))
            .Throws<InvalidOperationException>();
        DocumentPathValidator.EnsureSaveAsDiffersFromCentral("C:/x/y.rvt", @"C:\X\other.rvt");
        DocumentPathValidator.EnsureSafeOverwrite(false, false);
        await Assert.That(() => DocumentPathValidator.EnsureSafeOverwrite(true, false)).Throws<InvalidOperationException>();
        await Assert.That(() => DocumentPathValidator.EnsureSafeOverwrite(false, true)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Parse_SaveAsDoesNotCompareDocumentReferenceAsCentralPath()
    {
        var result = ControlJobParser.Parse("""{"command":"save-document","document":"C:\\x\\a.rvt","saveAs":"C:\\x\\a.rvt"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
    }

    [Test]
    public async Task Parse_FileActionsRejectUntrustedUncBeforeExecution()
    {
        foreach (var json in new[]
        {
            """{"command":"open-document","path":"\\\\srv\\share\\a.rvt"}""",
            """{"command":"save-document","document":"A","saveAs":"\\\\srv\\share\\a.rvt"}""",
            """{"command":"export-nwc","path":"\\\\srv\\share\\a.nwc"}""",
            """{"command":"export-nwc","path":"C:\\x\\a.nwc","settingsXml":"\\\\srv\\share\\settings.xml"}""",
            """{"command":"edit-families","operations":[{"op":"add_shared_parameters","sharedParameterFile":"\\\\srv\\share\\a.txt","parameters":[{"name":"A","group":"Data"}]}]}"""
        })
        {
            var result = ControlJobParser.Parse(json);
            await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
            await Assert.That(result.Error).Contains("trustedNetworkRoots");
            await Assert.That(result.Error).DoesNotContain("srv");
        }
        foreach (var json in new[]
        {
            """{"command":"open-document","path":"\\\\SRV\\Share\\a.rvt"}""",
            """{"command":"save-document","document":"A","saveAs":"\\\\SRV\\Share\\a.rvt"}""",
            """{"command":"export-nwc","path":"\\\\SRV\\Share\\a.nwc"}""",
            """{"command":"export-nwc","path":"C:\\x\\a.nwc","settingsXml":"\\\\SRV\\Share\\settings.xml"}""",
            """{"command":"edit-families","operations":[{"op":"add_shared_parameters","sharedParameterFile":"\\\\SRV\\Share\\a.txt","parameters":[{"name":"A","group":"Data"}]}]}"""
        })
            await Assert.That(ControlJobParser.Parse(json, [@"\\srv\share"]).Kind).IsEqualTo(ControlJobKind.Action);
        var untrusted = ControlJobParser.Parse("""{"command":"open-document","path":"\\\\srv\\share\\a.rvt"}""");
        await Assert.That(untrusted.Error).IsEqualTo(
            @"path is a network path. Add its share to trustedNetworkRoots in %LOCALAPPDATA%\RevitModelMcp\settings.json to allow it.");
    }

    [Test]
    public async Task Documents_UsesReadRoutingWithoutAnActiveDocument()
    {
        var parsed = ControlJobParser.Parse("""{"command":"documents"}""");
        await Assert.That(parsed.Kind).IsEqualTo(ControlJobKind.Documents);
        await Assert.That(ActionJobParser.IsAction("documents")).IsFalse();
    }

    [Test]
    public async Task Parse_NwcDefaultsMatchExporterDefaults()
    {
        var result = ControlJobParser.Parse("""{"command":"export-nwc","path":"C:\\x\\a.nwc"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        var options = result.Action!.Nwc;
        await Assert.That(options.Scope).IsEqualTo("model");
        await Assert.That(options.Coordinates).IsEqualTo("shared");
        await Assert.That(options.Parameters).IsEqualTo("all");
        await Assert.That(options.ExportElementIds).IsTrue();
        await Assert.That(options.ConvertElementProperties).IsFalse();
        await Assert.That(options.ExportParts).IsFalse();
        await Assert.That(options.ExportRoomAsAttribute).IsTrue();
        await Assert.That(options.ExportRoomGeometry).IsTrue();
        await Assert.That(options.ConvertLights).IsFalse();
        await Assert.That(options.ConvertLinkedCadFormats).IsTrue();
        await Assert.That(options.ExportLinks).IsFalse();
        await Assert.That(options.ExportUrls).IsTrue();
        await Assert.That(options.DivideFileIntoLevels).IsTrue();
        await Assert.That(options.FindMissingMaterials).IsTrue();
        await Assert.That(options.FacetingFactor).IsEqualTo(1);
        await Assert.That(options.Overwrite).IsFalse();
    }

    [Test]
    public async Task Parse_NwcAcceptsParameterOverrideAndSelection()
    {
        var result = ControlJobParser.Parse("""{"command":"export-nwc","path":"C:\\x\\a.nwc","scope":"selection","elementIds":[1,2],"parameters":"none","facetingFactor":5,"overwrite":true}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.Nwc.Parameters).IsEqualTo("none");
        await Assert.That(result.Action.Nwc.FacetingFactor).IsEqualTo(5);
        await Assert.That(result.Action.Nwc.Overwrite).IsTrue();
        await Assert.That(result.Action.ElementIds).IsEquivalentTo(new long[] { 1, 2 });
    }

    [Test]
    public async Task Parse_NwcRejectsNonStringParameters()
    {
        var result = ControlJobParser.Parse("""{"command":"export-nwc","path":"C:\\x\\a.nwc","parameters":["none"]}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Cause).IsNull();
    }

    [Test]
    public async Task Serialize_NwcResponse_UsesSnakeCaseOptions()
    {
        var data = new ActionResultData
        {
            Path = @"C:\x\a.nwc",
            Scope = "model",
            DryRun = true,
            Overwritten = false,
            Options = new NwcOptionsResult { Scope = "model", Coordinates = "shared", Parameters = "all", FacetingFactor = 1 }
        };
        var json = CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok("export-nwc", data, 1));
        await Assert.That(json).Contains("\"export_element_ids\":false");
        await Assert.That(json).Contains("\"faceting_factor\":1");
        await Assert.That(json).Contains("\"view\":null");
    }

    [Test]
    [Arguments("\"scope\":\"view\"")]
    [Arguments("\"scope\":\"selection\",\"elementIds\":[]")]
    [Arguments("\"coordinates\":\"unknown\"")]
    [Arguments("\"parameters\":\"unknown\"")]
    [Arguments("\"facetingFactor\":0")]
    [Arguments("\"facetingFactor\":101")]
    public async Task Parse_NwcRejectsInvalidOptions(string fields)
    {
        var result = ControlJobParser.Parse($$"""{"command":"export-nwc","path":"C:\\x\\a.nwc",{{fields}}}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task Parse_BatchRejectsNwcExport()
    {
        var result = ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"export-nwc","path":"C:\\x\\a.nwc"}]}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    [Arguments("""{"command":"edit-families","operations":[]}""")]
    [Arguments("""{"command":"edit-families","operations":[{"op":"unknown"}]}""")]
    [Arguments("""{"command":"edit-families","operations":[{"op":"remove_parameters","names":[]}]}""")]
    [Arguments("""{"command":"batch","steps":[{"command":"edit-families","operations":[{"op":"purge"}]}]}""")]
    public async Task Parse_InvalidFamilyEdits_AreRejected(string json)
    {
        await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task Parse_FamilyWildcardAndDefaults()
    {
        var result = ControlJobParser.Parse("""{"command":"edit-families","families":["*"],"operations":[{"op":"purge"}]}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.Families).IsEquivalentTo(new[] { "*" });
        await Assert.That(result.Action.StopOnError).IsTrue();
        await Assert.That(result.Action.OverwriteParameterValues).IsFalse();
    }

    [Test]
    public async Task Parse_SharedParameterInstanceDefaultsToTrue()
    {
        var result = ControlJobParser.Parse("""{"command":"edit-families","operations":[{"op":"add_shared_parameters","parameters":[{"name":"AssetId","group":"Data"}]}]}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.Operations[0].Parameters![0].Instance).IsTrue();

        var explicitType = ControlJobParser.Parse("""{"command":"edit-families","operations":[{"op":"add_shared_parameters","parameters":[{"name":"AssetId","group":"Data","instance":false}]}]}""");
        await Assert.That(explicitType.Action!.Operations[0].Parameters![0].Instance).IsFalse();
    }

    [Test]
    public async Task Parse_FamilyAudit_UsesReadRoutingAndKeepsAddressedDocument()
    {
        var result = ControlJobParser.Parse("""{"command":"family-audit","families":["Door"],"targetDocument":"Model"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.FamilyAudit);
        await Assert.That(ActionJobParser.IsAction(result.Command)).IsFalse();
        await Assert.That(result.CoordinatorJob.TargetDocument).IsEqualTo("Model");
        await Assert.That(result.TargetDocument).IsNull();
        await Assert.That(result.Action!.Families).IsEquivalentTo(new[] { "Door" });
    }

    [Test]
    public async Task Parse_MoreThanTwoHundredFamilies_IsRejected()
    {
        var names = string.Join(",", Enumerable.Range(0, 201).Select(index => $"\"Family {index}\""));
        var result = ControlJobParser.Parse($$"""{"command":"family-audit","families":[{{names}}]}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task ValidateFamilyMode_RequiresNamesOnlyForProject()
    {
        var action = new ActionJobContract();
        await Assert.That(() => ActionJobParser.ValidateFamilyMode(action, false)).Throws<ArgumentException>();
        ActionJobParser.ValidateFamilyMode(action, true);
        action.Families = ["*"];
        ActionJobParser.ValidateFamilyMode(action, false);
        await Assert.That(() => ActionJobParser.ValidateFamilyMode(action, true)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_AlignLinkDatums_UsesDefaultsAndRejectsBatchStep()
    {
        var parsed = ControlJobParser.Parse("""{"command":"align-link-datums","link":"AR.rvt : 1"}""");
        await Assert.That(parsed.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(parsed.Action!.DatumOptions!.Kinds).IsEquivalentTo(new[] { "grids", "levels" });
        await Assert.That(parsed.Action.DatumOptions.ToleranceMm).IsEqualTo(0.5);
        await Assert.That(parsed.Action.DatumOptions.CreateMissing).IsTrue();
        var mapped = ControlJobParser.Parse("""{"command":"align-link-datums","link":"AR.rvt","nameMap":{"A":"Host A"}}""");
        await Assert.That(mapped.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(mapped.Action!.DatumOptions!.NameMap["A"]).IsEqualTo("Host A");
        var batch = ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"align-link-datums","link":"AR.rvt"}]}""");
        await Assert.That(batch.Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task Parse_AlignLinkDatums_RejectsInvalidOptions()
    {
        foreach (var payload in new[]
        {
            """{"command":"align-link-datums"}""",
            """{"command":"align-link-datums","link":"A","kinds":["walls"]}""",
            """{"command":"align-link-datums","link":"A","toleranceMm":0}"""
        })
            await Assert.That(ControlJobParser.Parse(payload).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    [Arguments("select")]
    [Arguments("show")]
    [Arguments("isolate")]
    [Arguments("delete")]
    public async Task Parse_ElementActions_DeduplicatesIds(string command)
    {
        var result = ControlJobParser.Parse($$"""{"command":"{{command}}","elementIds":[1,2,1],"targetDocument":" Model A ","targetProcessId":42}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.ElementIds).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(result.Action.Select).IsTrue();
        await Assert.That(result.TargetProcessId).IsEqualTo(42);
        await Assert.That(result.TargetDocument).IsEqualTo("Model A");
    }

    [Test]
    public async Task Parse_Move_PreservesMillimetersAndDefaultsZ()
    {
        var result = ControlJobParser.Parse("""{"command":"move","elementIds":[2147483648],"dxMm":304.8,"dyMm":-200}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.DxMm).IsEqualTo(304.8);
        await Assert.That(result.Action.DyMm).IsEqualTo(-200);
        await Assert.That(result.Action.DzMm).IsEqualTo(0);
        await Assert.That(result.Action.ElementIds[0]).IsEqualTo(2147483648L);
    }

    [Test]
    public async Task Parse_CreateWall_PreservesEndpointsAndDefaults()
    {
        var result = ControlJobParser.Parse("""{"command":"create-wall","startMm":[0,10],"endMm":[2000,-20],"level":"01"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.StartMm).IsEquivalentTo(new double[] { 0, 10 });
        await Assert.That(result.Action.EndMm).IsEquivalentTo(new double[] { 2000, -20 });
        await Assert.That(result.Action.HeightMm).IsEqualTo(3000);
        await Assert.That(result.Action.WallType).IsNull();
    }

    [Test]
    public async Task Parse_PlaceFamily_PreservesTypeLevelAndRotation()
    {
        var result = ControlJobParser.Parse("""{"command":"place-family","family":"Desk","typeName":"1200","xMm":100,"yMm":200,"level":"01","rotationDeg":90}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.Family).IsEqualTo("Desk");
        await Assert.That(result.Action.TypeName).IsEqualTo("1200");
        await Assert.That(result.Action.Level).IsEqualTo("01");
        await Assert.That(result.Action.XMm).IsEqualTo(100);
        await Assert.That(result.Action.YMm).IsEqualTo(200);
        await Assert.That(result.Action.RotationDeg).IsEqualTo(90);
    }

    [Test]
    [Arguments("""{"command":"select","elementIds":[]}""")]
    [Arguments("""{"command":"isolate","elementIds":[],"reset":true}""")]
    [Arguments("""{"command":"set-parameter","elementId":1,"parameter":"Comments","value":""}""")]
    public async Task Parse_EmptyValuesAllowedForClearing(string json)
    {
        await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Action);
    }

    [Test]
    [Arguments("""{"command":"show","elementIds":[]}""")]
    [Arguments("""{"command":"select"}""")]
    [Arguments("""{"command":"select","elementIds":[-1]}""")]
    [Arguments("""{"command":"select","elementIds":[1.5]}""")]
    [Arguments("""{"command":"delete","elementIds":[0]}""")]
    [Arguments("""{"command":"isolate","elementIds":[]}""")]
    [Arguments("""{"command":"move","elementIds":[1],"dxMm":0}""")]
    [Arguments("""{"command":"move","elementIds":[1],"dxMm":0,"dyMm":0,"dzMm":"INF"}""")]
    [Arguments("""{"command":"place-family","family":"Desk","xMm":0,"yMm":0}""")]
    [Arguments("""{"command":"place-family","family":" ","xMm":0,"yMm":0,"level":"01"}""")]
    [Arguments("""{"command":"create-wall","startMm":[0],"endMm":[1,2],"level":"01"}""")]
    [Arguments("""{"command":"create-wall","startMm":[0,0],"endMm":[0,0],"level":"01"}""")]
    [Arguments("""{"command":"create-wall","startMm":[0,0],"endMm":[1,2],"level":"01","heightMm":0}""")]
    [Arguments("""{"command":"set-parameter","elementId":1,"parameter":"Comments"}""")]
    [Arguments("""{"command":"set-parameter","elementId":0,"parameter":"Comments","value":"x"}""")]
    public async Task Parse_InvalidActionsAreRejected(string json)
    {
        var result = ControlJobParser.Parse(json);
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(string.IsNullOrEmpty(result.Error)).IsFalse();
    }

    [Test]
    public async Task ClosestFamilies_RanksNamesAndLimitsResults()
    {
        var names = ActionJobParser.ClosestFamilyNames("desk", new[] { "Chair", "Desks", "Desk", "DESK", "Desk large", "Door", "Window", "Roof" });
        await Assert.That(names.Count).IsEqualTo(3);
        await Assert.That(names[0]).IsEqualTo("Desk");
        await Assert.That(names[1]).IsEqualTo("Desks");
    }

    [Test]
    public async Task ClosestFamilies_PrefersSubstringsAndRejectsUnrelatedNames()
    {
        var names = ActionJobParser.ClosestFamilyNames("Desk", new[] { "Fryer", "Task", "Desl", "Office Desk Adjustable", "DESK" });
        await Assert.That(names).IsEquivalentTo(new[] { "DESK", "Office Desk Adjustable", "Desl", "Task" });
        await Assert.That(names[0]).IsEqualTo("DESK");
        await Assert.That(names[1]).IsEqualTo("Office Desk Adjustable");
        await Assert.That(ActionJobParser.ClosestFamilyNames("Desk", new[] { "Fryer", "Window", "Chair" })).IsEmpty();
        await Assert.That(ActionJobParser.ClosestFamilyNames("Desk", Enumerable.Range(0, 10).Select(index => $"Desk {index}")).Count).IsEqualTo(5);
    }

    [Test]
    [Arguments("null")]
    [Arguments("\"1200\"")]
    public async Task Parse_PlaceFamily_SplitsFamilyAndType(string typeName)
    {
        var result = ControlJobParser.Parse($$"""{"command":"place-family","family":" desk : 1200 ","typeName":{{typeName}},"xMm":0,"yMm":0,"level":"01"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.Family).IsEqualTo("desk");
        await Assert.That(result.Action.TypeName).IsEqualTo("1200");
    }

    [Test]
    public async Task Parse_PlaceFamily_MatchesEmbeddedTypeCaseInsensitively()
    {
        var result = ControlJobParser.Parse("""{"command":"place-family","family":"Desk: LARGE","typeName":"large","xMm":0,"yMm":0,"level":"01"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.TypeName).IsEqualTo("LARGE");
    }

    [Test]
    [Arguments("Desk:", "null")]
    [Arguments(":1200", "null")]
    [Arguments("Desk:1200", "\"1500\"")]
    public async Task Parse_PlaceFamily_RejectsMalformedOrConflictingType(string family, string typeName)
    {
        var result = ControlJobParser.Parse($$"""{"command":"place-family","family":"{{family}}","typeName":{{typeName}},"xMm":0,"yMm":0,"level":"01"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task Serialize_ActionFailure_ContainsErrorAndView()
    {
        var response = CommandResponse<ActionResultData>.Fail("move", "actions disabled on the workstation", 0);
        response.Error = response.Message;
        response.ActiveView = "Level 1";
        var json = CommandResponseJsonSerializer.Serialize(response);
        await Assert.That(json).Contains("\"success\":false");
        await Assert.That(json).Contains("\"error\":\"actions disabled on the workstation\"");
        await Assert.That(json).Contains("\"activeView\":\"Level 1\"");
    }

    [Test]
    [Arguments(true, false, false, false, ActionFailureDisposition.DismissWarning)]
    [Arguments(true, false, false, true, ActionFailureDisposition.DismissWarning)]
    [Arguments(false, true, true, false, ActionFailureDisposition.ResolveError)]
    [Arguments(false, true, false, false, ActionFailureDisposition.RollBack)]
    [Arguments(false, true, true, true, ActionFailureDisposition.RollBack)]
    [Arguments(false, false, true, false, ActionFailureDisposition.RollBack)]
    public async Task ClassifyFailure_WarningsContinueAndUnsafeOrRepeatedErrorsRollBack(
        bool isWarning, bool isError, bool hasSafeResolution, bool resolutionAttempted,
        ActionFailureDisposition expected)
    {
        await Assert.That(ActionFailurePolicy.Classify(isWarning, isError, hasSafeResolution, resolutionAttempted))
            .IsEqualTo(expected);
    }

    [Test]
    [Arguments("move")]
    [Arguments("place-family")]
    [Arguments("create-wall")]
    [Arguments("set-parameter")]
    [Arguments("delete")]
    [Arguments("isolate")]
    public async Task Serialize_ActionSuccess_ReportsDismissedWarnings(string command)
    {
        var response = CommandResponse<ActionResultData>.Ok(command, new ActionResultData { Count = 1 }, 0);
        response.WarningsDismissed = ["Identical instances.", "Second warning."];
        using var json = System.Text.Json.JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
        await Assert.That(json.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(json.RootElement.GetProperty("warningsDismissed").EnumerateArray()
            .Select(warning => warning.GetString()).ToArray()).IsEquivalentTo(new string?[] { "Identical instances.", "Second warning." });
        await Assert.That(json.RootElement.GetProperty("data").GetProperty("count").GetInt32()).IsEqualTo(1);
        await Assert.That(json.RootElement.TryGetProperty("message", out _)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Serialize_ActionSuccess_OmitsAbsentOrEmptyWarnings(bool emptyList)
    {
        var response = CommandResponse<ActionResultData>.Ok("move", new ActionResultData { Count = 1 }, 0);
        response.WarningsDismissed = emptyList ? [] : null;
        using var json = System.Text.Json.JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
        await Assert.That(json.RootElement.TryGetProperty("warningsDismissed", out _)).IsFalse();
    }

    [Test]
    public async Task Serialize_RolledBackAction_ReportsErrorsWithoutSuccessData()
    {
        var response = CommandResponse<ActionResultData>.Fail("move", "First error.; Second error.", 0);
        using var json = System.Text.Json.JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
        await Assert.That(json.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(json.RootElement.GetProperty("message").GetString()).IsEqualTo("First error.; Second error.");
        await Assert.That(json.RootElement.TryGetProperty("data", out _)).IsFalse();
        await Assert.That(json.RootElement.TryGetProperty("warningsDismissed", out _)).IsFalse();
    }


    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Parse_DryRun_PreservesFlag(bool dryRun)
    {
        var result = ControlJobParser.Parse($$"""{"command":"move","elementIds":[1],"dxMm":1,"dyMm":0,"dryRun":{{dryRun.ToString().ToLowerInvariant()}}}""");
        await Assert.That(result.Action!.DryRun).IsEqualTo(dryRun);
    }

    [Test]
    public async Task Parse_Batch_ValidatesEachStep()
    {
        var result = ControlJobParser.Parse("""{"command":"batch","dryRun":true,"steps":[{"command":"move","elementIds":[1],"dxMm":10,"dyMm":0},{"command":"set-parameter","elementId":1,"parameter":"Comments","value":"Reviewed"}]}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.DryRun).IsTrue();
        await Assert.That(result.Action.Steps.Count).IsEqualTo(2);
        await Assert.That(result.Action.Steps[0].Command).IsEqualTo("move");
        await Assert.That(result.Action.Steps[0].Action!.DxMm).IsEqualTo(10);
        await Assert.That(result.Action.Steps[1].Action!.Value).IsEqualTo("Reviewed");
    }

    [Test]
    [Arguments("""{"command":"batch","steps":[]}""")]
    [Arguments("""{"command":"batch"}""")]
    [Arguments("""{"command":"batch","steps":[null]}""")]
    [Arguments("""{"command":"batch","steps":[{"command":"show","elementIds":[1]}]}""")]
    [Arguments("""{"command":"batch","steps":[{"command":"batch","steps":[]}]}""")]
    [Arguments("""{"command":"batch","steps":[{"command":"move","elementIds":[1]}]}""")]
    [Arguments("""{"command":"batch","steps":[{"command":"unknown"}]}""")]
    public async Task Parse_InvalidBatch_IsRejected(string json)
    {
        await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    [Arguments(50, ControlJobKind.Action)]
    [Arguments(51, ControlJobKind.Invalid)]
    public async Task Parse_Batch_EnforcesStepLimit(int count, ControlJobKind expected)
    {
        var steps = string.Join(",", Enumerable.Repeat("""{"command":"select","elementIds":[]}""", count));
        var result = ControlJobParser.Parse($$"""{"command":"batch","steps":[{{steps}}]}""");
        await Assert.That(result.Kind).IsEqualTo(expected);
    }

    [Test]
    public async Task WorksetMask_MatchesGlobAndRegexIgnoringCase()
    {
        await Assert.That(WorksetMask.Matches("HVAC Supply", "hvac*")).IsTrue();
        await Assert.That(WorksetMask.Matches("HVAC Supply", "regex:^hvac\\s+sup")).IsTrue();
        await Assert.That(WorksetMask.Matches("Structure", "hvac*")).IsFalse();
    }

    [Test]
    public async Task Parse_ViewVisibilityValidatesTemplateModeAndMasks()
    {
        var visibility = ControlJobParser.Parse("""{"command":"set-view-visibility","view":"3D","worksets":{"hideMask":["HVAC*"]}}""");
        await Assert.That(visibility.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(visibility.Action!.Visibility!.Worksets.HideMask).IsEquivalentTo(new[] { "HVAC*" });
        await Assert.That(ControlJobParser.Parse("""{"command":"set-view-visibility","view":"3D","categoryClasses":{"model":true},"templateMode":"detach"}""").Kind)
            .IsEqualTo(ControlJobKind.Action);
        await Assert.That(ControlJobParser.Parse("""{"command":"set-view-visibility","view":"3D","categoryClasses":{"model":true},"templateMode":"edit_all"}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"set-view-visibility","view":"3D","worksets":{"hideMask":["regex:["]}}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task CategoryTypeExpansion_ExpandsOnlyRequestedTypes()
    {
        var categories = new[] { ("Walls", "model"), ("Text", "annotation"), ("Imports", "import") };
        await Assert.That(CategoryTypeExpansion.Expand(["annotation", "import"], categories))
            .IsEquivalentTo(new[] { "Text", "Imports" });
    }

    [Test]
    public async Task Parse_RemoveLinksValidatesKinds()
    {
        await Assert.That(ControlJobParser.Parse("""{"command":"remove-links","links":["*"],"kinds":["revit","image"]}""").Kind)
            .IsEqualTo(ControlJobKind.Action);
        await Assert.That(ControlJobParser.Parse("""{"command":"remove-links","links":["*"],"kinds":["unknown"]}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task Parse_BatchRejectsViewVisibilityAndLinkRemovalSteps()
    {
        var visibilityBatch = ControlJobParser.Parse(
            """{"command":"batch","steps":[{"command":"set-view-visibility","view":"3D","categoryClasses":{"model":true}}]}""");
        await Assert.That(visibilityBatch.Kind).IsEqualTo(ControlJobKind.Invalid);
        var linkRemovalBatch = ControlJobParser.Parse(
            """{"command":"batch","steps":[{"command":"remove-links","links":["*"]}]}""");
        await Assert.That(linkRemovalBatch.Kind).IsEqualTo(ControlJobKind.Invalid);
    }
    [Test]
    public async Task ViewAndSheetActions_ValidateArgumentsAndBatchAllowlist()
    {
        foreach (var json in new[]
        {
            """{"command":"create-view","kind":"floor_plan","level":"L1"}""",
            """{"command":"create-view","kind":"section","box":{"minMm":[0,0,0],"maxMm":[100,100,100]}}""",
            """{"command":"create-view","kind":"3d","elementIds":[1,2]}""",
            """{"command":"duplicate-view","view":"Level 1","mode":"dependent"}""",
            """{"command":"apply-view-template","views":["Level 1"],"template":"Plan"}""",
            """{"command":"create-sheet","number":"A101","name":"Plan"}""",
            """{"command":"place-views-on-sheet","sheet":"A101","placements":[{"view":"Level 1"}]}"""
        })
            await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Action);
        foreach (var json in new[]
        {
            """{"command":"create-view","kind":"floor_plan"}""",
            """{"command":"create-view","kind":"section","box":{"minMm":[0,0,0],"maxMm":[0,1,1]}}""",
            """{"command":"create-view","kind":"3d","box":{"minMm":[0,0,0],"maxMm":[1,1,1]},"elementIds":[1]}""",
            """{"command":"duplicate-view","view":"a","mode":"bad"}""",
            """{"command":"apply-view-template","views":[],"template":"Plan"}""",
            """{"command":"create-sheet","number":"","name":"Plan"}""",
            """{"command":"place-views-on-sheet","sheet":"A101","placements":[{"view":"Level 1","xMm":1}]}""",
            """{"command":"batch","steps":[{"command":"place-views-on-sheet","sheet":"A101","placements":[{"view":"Level 1"}]}]}"""
        })
            await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"create-sheet","number":"A101","name":"Plan"}]}""").Kind)
            .IsEqualTo(ControlJobKind.Action);
    }

    [Test]
    public async Task CreateView_WholeModelDefaultsAndBatchStyles()
    {
        var result = ControlJobParser.Parse("""{"command":"create-view","kind":"3d"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.Box).IsNull();
        await Assert.That(result.Action.ElementIds).IsEmpty();
        await Assert.That(result.Action.DisplayStyle).IsEqualTo("shaded");
        await Assert.That(result.Action.DetailLevel).IsEqualTo("fine");
        var plan = ControlJobParser.Parse("""{"command":"create-view","kind":"floor_plan","level":"L1"}""");
        await Assert.That(plan.Action!.DisplayStyle).IsNull();
        await Assert.That(plan.Action.DetailLevel).IsNull();
        var batch = ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"create-view","kind":"3d","displayStyle":"hidden_line","detailLevel":"coarse"}]}""");
        await Assert.That(batch.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(batch.Action!.Steps[0].Action!.DisplayStyle).IsEqualTo("hidden_line");
        await Assert.That(batch.Action.Steps[0].Action!.DetailLevel).IsEqualTo("coarse");
    }

    [Test]
    [Arguments("""{"command":"create-view","kind":"section"}""")]
    [Arguments("""{"command":"create-view","kind":"3d","displayStyle":"wireframe"}""")]
    [Arguments("""{"command":"create-view","kind":"3d","detailLevel":"undefined"}""")]
    [Arguments("""{"command":"create-view","kind":"3d","elementIds":[]}""")]
    public async Task CreateView_RejectsInvalidStylesAndBounds(string json)
    {
        await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task SectionBoxBounds_CutPlanePassesThroughCenter()
    {
        var (minimum, maximum) = SectionBoxBounds.FromExtents(4000, 3000, 2000);
        await Assert.That(minimum).IsEquivalentTo(new double[] { -2000, -1000, -1500 });
        await Assert.That(maximum).IsEquivalentTo(new double[] { 2000, 1000, 0 });
    }

}
