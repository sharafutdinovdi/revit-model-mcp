using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class ActionSummaryBuilderTests
{
    [Test]
    public async Task BuildSummary_Move_ReportsCountAndDocument()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "move", DocumentTitle = "Project1.rvt", Count = 3
        });
        await Assert.That(summary).IsEqualTo("Moved 3 elements in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_Move_SingularElement()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "move", DocumentTitle = "Project1.rvt", Count = 1
        });
        await Assert.That(summary).IsEqualTo("Moved 1 element in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_MoveDryRun_UsesWouldPhrasing()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "move", DocumentTitle = "Project1.rvt", Count = 2, DryRun = true
        });
        await Assert.That(summary).IsEqualTo("Would move 2 elements in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_Delete_ReportsCount()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "delete", DocumentTitle = "Project1.rvt", Count = 5
        });
        await Assert.That(summary).IsEqualTo("Deleted 5 elements in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_Select_ReportsCount()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "select", DocumentTitle = "Project1.rvt", Count = 0
        });
        await Assert.That(summary).IsEqualTo("Selected 0 elements in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_IsolateReset_ReportsReset()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "isolate", DocumentTitle = "Project1.rvt", Count = 0
        });
        await Assert.That(summary).IsEqualTo("Reset temporary isolation in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_PlaceFamily_IncludesFamilyAndType()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "place-family", DocumentTitle = "Project1.rvt", Family = "Door", TypeName = "36x84"
        });
        await Assert.That(summary).IsEqualTo("Placed Door: 36x84 in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_CreateWall_IncludesWallType()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "create-wall", DocumentTitle = "Project1.rvt", WallType = "Generic 200mm"
        });
        await Assert.That(summary).IsEqualTo("Created a Generic 200mm wall in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_SetParameter_IncludesParameterName()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "set-parameter", DocumentTitle = "Project1.rvt", Parameter = "Comments"
        });
        await Assert.That(summary).IsEqualTo("Set parameter 'Comments' on 1 element in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_Batch_ReportsStepCount()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "batch", DocumentTitle = "Project1.rvt", BatchStepCount = 4
        });
        await Assert.That(summary).IsEqualTo("Ran a batch of 4 steps in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_EditFamilies_ReportsCount()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "edit-families", DocumentTitle = "Project1.rvt", Count = 2
        });
        await Assert.That(summary).IsEqualTo("Edited 2 families in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_UnknownCommand_FallsBackGenerically()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "align-link-datums", DocumentTitle = "Project1.rvt"
        });
        await Assert.That(summary).IsEqualTo("Aligned link datums in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_MissingDocumentTitle_FallsBackToTheModel()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext { Command = "move", Count = 1 });
        await Assert.That(summary).IsEqualTo("Moved 1 element in the model.");
    }

    [Test]
    public async Task BuildGroupName_PrefixesClientAndTruncatesSummary()
    {
        var longSummary = "Moved " + new string('9', 100) + " elements in Project1.rvt.";
        var name = ActionSummaryBuilder.BuildGroupName("claude-code", longSummary);
        await Assert.That(name.Length).IsLessThanOrEqualTo(60);
        await Assert.That(name).StartsWith("MCP (claude-code): ");
    }

    [Test]
    public async Task BuildGroupName_ShortSummary_KeptVerbatimWithoutEllipsis()
    {
        var name = ActionSummaryBuilder.BuildGroupName("claude-code", "Moved 3 elements in Project1.rvt.");
        await Assert.That(name).IsEqualTo("MCP (claude-code): Moved 3 elements in Project1.rvt");
        await Assert.That(name.Length).IsLessThanOrEqualTo(60);
    }

    [Test]
    public async Task BuildGroupName_BlankClientName_FallsBackToUnknown()
    {
        var name = ActionSummaryBuilder.BuildGroupName("  ", "Moved 3 elements in Project1.rvt.");
        await Assert.That(name).StartsWith("MCP (unknown): ");
    }

    [Test]
    public async Task BuildGroupName_NeverExceedsSixtyCharacters()
    {
        var name = ActionSummaryBuilder.BuildGroupName(
            new string('c', 80), "Moved 3 elements in a very long document title that keeps going.rvt.");
        await Assert.That(name.Length).IsLessThanOrEqualTo(60);
    }
}
