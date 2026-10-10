using System.Text.Json;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class TagAndScheduleActionTests
{
    [Test]
    [Arguments("""{"command":"tag-elements","view":"Level 1","categories":["Doors"]}""")]
    [Arguments("""{"command":"tag-elements","view":"Level 1","elementIds":[5,6],"leader":true,"orientation":"vertical","typeName":"Door Tag","skipTagged":false}""")]
    [Arguments("""{"command":"create-schedule","category":"Doors","scheduleFields":["Mark","Level"]}""")]
    [Arguments("""{"command":"create-schedule","category":"Doors","scheduleFields":["Mark"],"name":"Door list","itemized":false,"scheduleFilters":[{"field":"Level","operator":"equal","value":"Level 1"},{"field":"Mark","operator":"has_value"}],"scheduleSort":[{"field":"Level","header":true},{"field":"Mark","descending":true}]}""")]
    public async Task ValidJobs_ParseAsActions(string json)
    {
        var result = ControlJobParser.Parse(json);
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Action);
        await Assert.That(result.Error).IsNull();
    }

    [Test]
    [Arguments("""{"command":"tag-elements","categories":["Doors"]}""")]
    [Arguments("""{"command":"tag-elements","view":"Level 1"}""")]
    [Arguments("""{"command":"tag-elements","view":"Level 1","categories":["Doors"],"elementIds":[1]}""")]
    [Arguments("""{"command":"tag-elements","view":"Level 1","categories":[" "]}""")]
    [Arguments("""{"command":"tag-elements","view":"Level 1","elementIds":[0]}""")]
    [Arguments("""{"command":"tag-elements","view":"Level 1","categories":["Doors"],"orientation":"diagonal"}""")]
    [Arguments("""{"command":"tag-elements","view":"Level 1","categories":["Doors"],"typeName":" "}""")]
    [Arguments("""{"command":"tag-elements","view":"Level 1","categories":["Doors"],"maxElements":0}""")]
    [Arguments("""{"command":"create-schedule","scheduleFields":["Mark"]}""")]
    [Arguments("""{"command":"create-schedule","category":"Doors","scheduleFields":[]}""")]
    [Arguments("""{"command":"create-schedule","category":"Doors","scheduleFields":["Mark"],"name":" "}""")]
    [Arguments("""{"command":"create-schedule","category":"Doors","scheduleFields":["Mark"],"scheduleFilters":[{"field":"Mark","operator":"equal"}]}""")]
    [Arguments("""{"command":"create-schedule","category":"Doors","scheduleFields":["Mark"],"scheduleFilters":[{"field":"Mark","operator":"has_value","value":"x"}]}""")]
    [Arguments("""{"command":"create-schedule","category":"Doors","scheduleFields":["Mark"],"scheduleFilters":[{"field":"Mark","operator":"like","value":"x"}]}""")]
    [Arguments("""{"command":"create-schedule","category":"Doors","scheduleFields":["Mark"],"scheduleSort":[{"field":" "}]}""")]
    [Arguments("""{"command":"batch","steps":[{"command":"tag-elements","view":"Level 1","categories":["Doors"]}]}""")]
    [Arguments("""{"command":"batch","steps":[{"command":"create-schedule","category":"Doors","scheduleFields":["Mark"]}]}""")]
    public async Task InvalidJobs_AreRejected(string json)
    {
        await Assert.That(ControlJobParser.Parse(json).Kind).IsEqualTo(ControlJobKind.Invalid);
    }

    [Test]
    public async Task TagElements_AppliesDefaults()
    {
        var job = ActionJobParser.Parse("tag-elements", new ControlJobContract
        {
            Command = "tag-elements",
            View = "Level 1",
            Categories = ["Doors"]
        });
        var action = job.Action!;
        await Assert.That(action.Leader).IsFalse();
        await Assert.That(action.Orientation).IsEqualTo("horizontal");
        await Assert.That(action.SkipTagged).IsTrue();
        await Assert.That(action.MaxElements).IsEqualTo(5000);
        await Assert.That(action.View).IsEqualTo("Level 1");
    }

    [Test]
    public async Task CreateSchedule_CarriesFiltersSortAndDefaults()
    {
        var job = ControlJobParser.Parse(
            """{"command":"create-schedule","category":"Doors","scheduleFields":["Mark"],"scheduleFilters":[{"field":"Level","operator":"equal","value":"L1"}],"scheduleSort":[{"field":"Level","header":true,"blankLine":true}]}""");
        var action = job.Action!;
        await Assert.That(action.Category).IsEqualTo("Doors");
        await Assert.That(action.Itemized).IsTrue();
        await Assert.That(action.ScheduleFields!.Single()).IsEqualTo("Mark");
        await Assert.That(action.ScheduleFilters!.Single().Operator).IsEqualTo("equal");
        await Assert.That(action.ScheduleFilters!.Single().Value).IsEqualTo("L1");
        await Assert.That(action.ScheduleSort!.Single().Header).IsTrue();
        await Assert.That(action.ScheduleSort!.Single().BlankLine).IsTrue();
        await Assert.That(action.ScheduleSort!.Single().Descending).IsFalse();
    }

    [Test]
    public async Task BuildSummary_TagAndSchedule()
    {
        var tags = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "tag-elements",
            DocumentTitle = "Project1.rvt",
            Count = 3,
            ViewName = "Level 1"
        });
        await Assert.That(tags).IsEqualTo("Created 3 tags in view 'Level 1' in Project1.rvt.");
        var preview = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "create-schedule",
            DocumentTitle = "Project1.rvt",
            Count = 1,
            ViewName = "Door list",
            DryRun = true
        });
        await Assert.That(preview).IsEqualTo("Would create schedule 'Door list' with 1 field in Project1.rvt.");
    }

    [Test]
    public async Task Serialize_TagAndScheduleResults_UseContractNames()
    {
        var tags = new ActionResultData { Count = 2, CreatedElementIds = [11, 12], Skipped = new List<string> { "Element 5: not found." } };
        using var tagJson = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok("tag-elements", tags, 1)));
        var tagData = tagJson.RootElement.GetProperty("data");
        await Assert.That(tagData.GetProperty("createdElementIds").EnumerateArray().Select(id => id.GetInt64())).IsEquivalentTo(new long[] { 11, 12 });
        await Assert.That(tagData.GetProperty("skipped")[0].GetString()).IsEqualTo("Element 5: not found.");

        var schedule = new ActionResultData
        {
            ViewId = 70,
            Fields = [new ScheduleFieldResult { Requested = "mark", Name = "Mark" }],
            FiltersApplied = 1,
            SortApplied = 0
        };
        using var scheduleJson = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(CommandResponse<ActionResultData>.Ok("create-schedule", schedule, 1)));
        var scheduleData = scheduleJson.RootElement.GetProperty("data");
        await Assert.That(scheduleData.GetProperty("viewId").GetInt64()).IsEqualTo(70);
        await Assert.That(scheduleData.GetProperty("fields")[0].GetProperty("name").GetString()).IsEqualTo("Mark");
        await Assert.That(scheduleData.GetProperty("filtersApplied").GetInt32()).IsEqualTo(1);
    }
}
