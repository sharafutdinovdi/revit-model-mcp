using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class ActionSummaryBuilderTests
{
    [Test]
    public async Task CreateMepRun_ReportsSegmentsAndKind()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "create-mep-run",
            DocumentTitle = "Model.rvt",
            ViewKind = "cable_tray",
            Count = 4
        });
        await Assert.That(summary).IsEqualTo("Created 4 segments of cable tray in Model.rvt.");
        await Assert.That(ActionSummaryBuilder.BuildGroupName("client", summary)).StartsWith("MCP (client): Created");
    }

    [Test]
    public async Task CadActions_ReportNamedUndoSummaries()
    {
        await Assert.That(ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "link-cad",
            DocumentTitle = "Project.rvt",
            CadLink = true
        })).IsEqualTo("Linked CAD in Project.rvt.");
        await Assert.That(ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "link-cad",
            DocumentTitle = "Project.rvt",
            CadLink = false,
            DryRun = true
        })).IsEqualTo("Would import CAD in Project.rvt.");
        await Assert.That(ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "walls-from-cad",
            DocumentTitle = "Project.rvt",
            Count = 3,
            DryRun = true
        })).IsEqualTo("Would create 3 walls from CAD in Project.rvt.");
    }

    [Test]
    public async Task ProcessModels_ReportsTotals()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "process-models",
            Count = 2,
            ProcessTotal = 3,
            ProcessFailed = 1
        });
        await Assert.That(summary).IsEqualTo("Processed 2 of 3 models; 1 failed, 0 skipped.");
    }

    [Test]
    [Arguments(0, "Processed 1 of 4 models; 0 failed, 0 skipped.")]
    [Arguments(3, "Processed 1 of 4 models; 0 failed, 0 skipped, 3 cancelled.")]
    public async Task ProcessModels_ReportsCancelledCount(int cancelled, string expected)
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "process-models",
            Count = 1,
            ProcessTotal = 4,
            ProcessCancelled = cancelled
        });
        await Assert.That(summary).IsEqualTo(expected);
    }

    [Test]
    public async Task ExecuteCode_UsesNamedUndoEntry()
    {
        await Assert.That(ActionSummaryBuilder.BuildGroupName("client", "Execute code"))
            .IsEqualTo("MCP (client): Execute code");
        await Assert.That(ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "execute-code",
            DocumentTitle = "Model.rvt"
        })).IsEqualTo("Executed code in Model.rvt.");
    }

    [Test]
    public async Task ExecuteCode_WithoutChanges_SaysNothingWasKept()
    {
        await Assert.That(ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "execute-code",
            DocumentTitle = "Model.rvt",
            NoChanges = true
        })).IsEqualTo("Executed code in Model.rvt. It changed nothing, so no undo entry was kept.");
        await Assert.That(ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "execute-code",
            DocumentTitle = "Model.rvt",
            DryRun = true,
            NoChanges = true
        })).IsEqualTo("Ran a code preview in Model.rvt.");
    }

    [Test]
    [Arguments(true, "Would export an NWC file from Model.rvt.")]
    [Arguments(false, "Exported an NWC file from Model.rvt.")]
    public async Task ExportNwc_DryRunUsesFutureTense(bool dryRun, string expected)
    {
        await Assert.That(ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "export-nwc",
            DocumentTitle = "Model.rvt",
            DryRun = dryRun
        })).IsEqualTo(expected);
    }

    [Test]
    [Arguments("export-nwc")]
    [Arguments("edit-families")]
    [Arguments("align-link-datums")]
    [Arguments("remove-links")]
    [Arguments("set-view-visibility")]
    [Arguments("batch")]
    public async Task DryRunSummariesNeverUsePastTense(string command)
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = command,
            DocumentTitle = "Model.rvt",
            DryRun = true,
            Count = 2,
            BatchStepCount = 2
        });
        await Assert.That(summary).StartsWith("Would ");
    }

    [Test]
    [Arguments(CodeFailureKind.Compilation, "Code failed to compile in Model.rvt.")]
    [Arguments(CodeFailureKind.Execution, "Code failed in Model.rvt.")]
    public async Task ExecuteCode_FailureSummaryDescribesFailure(CodeFailureKind failure, string expected)
    {
        await Assert.That(ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "execute-code",
            DocumentTitle = "Model.rvt",
            CodeFailure = failure
        })).IsEqualTo(expected);
    }

    [Test]
    public async Task BuildSummary_Move_ReportsCountAndDocument()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "move",
            DocumentTitle = "Project1.rvt",
            Count = 3
        });
        await Assert.That(summary).IsEqualTo("Moved 3 elements in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_Move_SingularElement()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "move",
            DocumentTitle = "Project1.rvt",
            Count = 1
        });
        await Assert.That(summary).IsEqualTo("Moved 1 element in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_MoveDryRun_UsesWouldPhrasing()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "move",
            DocumentTitle = "Project1.rvt",
            Count = 2,
            DryRun = true
        });
        await Assert.That(summary).IsEqualTo("Would move 2 elements in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_Delete_ReportsCount()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "delete",
            DocumentTitle = "Project1.rvt",
            Count = 5
        });
        await Assert.That(summary).IsEqualTo("Deleted 5 elements in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_Select_ReportsCount()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "select",
            DocumentTitle = "Project1.rvt",
            Count = 0
        });
        await Assert.That(summary).IsEqualTo("Selected 0 elements in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_IsolateReset_ReportsReset()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "isolate",
            DocumentTitle = "Project1.rvt",
            Count = 0
        });
        await Assert.That(summary).IsEqualTo("Reset temporary isolation in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_PlaceFamily_IncludesFamilyAndType()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "place-family",
            DocumentTitle = "Project1.rvt",
            Family = "Door",
            TypeName = "36x84"
        });
        await Assert.That(summary).IsEqualTo("Placed Door: 36x84 in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_CreateWall_IncludesWallType()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "create-wall",
            DocumentTitle = "Project1.rvt",
            WallType = "Generic 200mm"
        });
        await Assert.That(summary).IsEqualTo("Created a Generic 200mm wall in Project1.rvt.");
    }

    [Test]
    [Arguments("floor_plan", "Night L2 plan", "Created floor plan 'Night L2 plan' in Project1.rvt.")]
    [Arguments("section", "Night section", "Created section 'Night section' in Project1.rvt.")]
    public async Task BuildSummary_CreateView_UsesKindAndName(string kind, string name, string expected)
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "create-view",
            DocumentTitle = "Project1.rvt",
            ViewKind = kind,
            ViewName = name
        });
        await Assert.That(summary).IsEqualTo(expected);
    }

    [Test]
    public async Task BuildSummary_CreateSheet_UsesNumberAndName()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "create-sheet",
            DocumentTitle = "Project1.rvt",
            SheetNumber = "NX-101",
            ViewName = "Night sheet"
        });
        await Assert.That(summary).IsEqualTo("Created sheet 'NX-101 - Night sheet' in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_SetParameter_IncludesParameterName()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "set-parameter",
            DocumentTitle = "Project1.rvt",
            Parameter = "Comments"
        });
        await Assert.That(summary).IsEqualTo("Set parameter 'Comments' on 1 element in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_Batch_ReportsStepCount()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "batch",
            DocumentTitle = "Project1.rvt",
            BatchStepCount = 4
        });
        await Assert.That(summary).IsEqualTo("Ran a batch of 4 steps in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_EditFamilies_ReportsCount()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "edit-families",
            DocumentTitle = "Project1.rvt",
            Count = 2
        });
        await Assert.That(summary).IsEqualTo("Edited 2 families in Project1.rvt.");
    }

    [Test]
    public async Task BuildSummary_UnknownCommand_FallsBackGenerically()
    {
        var summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "align-link-datums",
            DocumentTitle = "Project1.rvt"
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
