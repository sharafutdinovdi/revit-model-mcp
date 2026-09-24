using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ActionJobParserTests
{
    [Test]
    public async Task DocumentConfirmationTokens_AreSingleUseBoundAndExpire()
    {
        var now = DateTimeOffset.UtcNow;
        var tokens = new DocumentConfirmationTokens(() => now);
        var first = tokens.Issue("save-document", "document-1", "path=A");
        await Assert.That(tokens.Consume(first, "save-document", "document-1", "path=B")).IsFalse();
        await Assert.That(tokens.Consume(first, "save-document", "document-1", "path=A")).IsFalse();
        var second = tokens.Issue("save-document", "document-1", "path=A");
        await Assert.That(tokens.Consume(second, "save-document", "document-1", "path=A")).IsTrue();
        await Assert.That(tokens.Consume(second, "save-document", "document-1", "path=A")).IsFalse();
        var third = tokens.Issue("save-document", "document-1", "path=A");
        now = now.AddMinutes(5);
        await Assert.That(tokens.Consume(third, "save-document", "document-1", "path=A")).IsFalse();
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
            """{"command":"save-document","document":"C:\\x\\a.rvt","saveAs":"C:\\x\\a.rvt"}"""
        })
            await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(ControlJobParser.Parse("""{"command":"batch","steps":[{"command":"save-document","document":"A"}]}""").Kind)
            .IsEqualTo(ControlJobKind.Invalid);
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
            Path = @"C:\x\a.nwc", Scope = "model", DryRun = true, Overwritten = false,
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
}
