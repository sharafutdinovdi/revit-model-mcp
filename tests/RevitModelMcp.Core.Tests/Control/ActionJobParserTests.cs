using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ActionJobParserTests
{
    [Test]
    [Arguments("select")]
    [Arguments("show")]
    [Arguments("isolate")]
    [Arguments("delete")]
    public async Task Parse_ElementActions_DeduplicatesIds(string command)
    {
        var result = ControlJobParser.Parse($$"""{"command":"{{command}}","elementIds":[1,2,1],"targetProcessId":42}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Action!.ElementIds).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(result.Action.Select).IsTrue();
        await Assert.That(result.TargetProcessId).IsEqualTo(42);
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
        await Assert.That(names.Count).IsEqualTo(5);
        await Assert.That(names[0]).IsEqualTo("Desk");
        await Assert.That(names[1]).IsEqualTo("Desks");
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
}
