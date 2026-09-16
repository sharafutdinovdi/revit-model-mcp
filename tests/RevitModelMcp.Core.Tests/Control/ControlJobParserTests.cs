using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Formatting;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ControlJobParserTests
{
    [Test]
    public async Task Parse_ModelHealth_ReturnsReadCommand()
    {
        var result = ControlJobParser.Parse("""{"command":"model-health"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ModelHealth);
        await Assert.That(result.Command).IsEqualTo("model-health");
    }

    [Test]
    public async Task Parse_LinksStatus_ReturnsReadCommand()
    {
        var result = ControlJobParser.Parse("""{"command":"links-status"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.LinksStatus);
        await Assert.That(result.Command).IsEqualTo("links-status");
    }

    [Test]
    public async Task Parse_SharedCoordinates_ReturnsReadCommand()
    {
        var result = ControlJobParser.Parse("""{"command":"shared-coordinates"}""");
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.SharedCoordinates);
        await Assert.That(result.Command).IsEqualTo("shared-coordinates");
    }

    [Test]
    public async Task Parse_ParameterFill_PreservesScopeAndDefaults()
    {
        var result = ControlJobParser.Parse("""
            {"command":"parameter-fill-check","categories":[" Walls "],"parameters":[" Mark "],
             "level":" Level 1 ","workset":" Shell ","view":" Plan "}
            """);
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ParameterFillCheck);
        await Assert.That(result.CoordinatorJob.Categories![0]).IsEqualTo("Walls");
        await Assert.That(result.CoordinatorJob.Parameters![0]).IsEqualTo("Mark");
        await Assert.That(result.CoordinatorJob.Level).IsEqualTo("Level 1");
        await Assert.That(result.CoordinatorJob.Workset).IsEqualTo("Shell");
        await Assert.That(result.CoordinatorJob.View).IsEqualTo("Plan");
        await Assert.That(result.CoordinatorJob.SampleLimit).IsEqualTo(20);
        await Assert.That(result.CoordinatorJob.IncludeTypes).IsTrue();
    }

    [Test]
    public async Task Parse_ParameterFill_RejectsInvalidBounds()
    {
        foreach (var job in new[]
        {
            new ControlJobContract { Parameters = ["Mark"] },
            new ControlJobContract { Categories = ["Walls"] },
            new ControlJobContract { Categories = [" "], Parameters = ["Mark"] },
            new ControlJobContract { Categories = Enumerable.Range(0, 21).Select(index => index.ToString()).ToList(), Parameters = ["Mark"] },
            new ControlJobContract { Categories = ["Walls"], Parameters = Enumerable.Range(0, 31).Select(index => index.ToString()).ToList() },
            new ControlJobContract { Categories = ["Walls"], Parameters = ["Mark"], SampleLimit = 0 },
            new ControlJobContract { Categories = ["Walls"], Parameters = ["Mark"], SampleLimit = 101 }
        })
        {
            job.Command = "parameter-fill-check";
            await Assert.That(ControlJobParseResult.FromContract(job).Kind).IsEqualTo(ControlJobKind.Invalid);
        }
        var valid = ControlJobParseResult.FromContract(new ControlJobContract
        {
            Command = "parameter-fill-check",
            Categories = ["Walls"],
            Parameters = ["Mark"],
            SampleLimit = 100,
            IncludeTypes = false
        });
        await Assert.That(valid.CoordinatorJob.SampleLimit).IsEqualTo(100);
        await Assert.That(valid.CoordinatorJob.IncludeTypes).IsFalse();
    }

    [Test]
    public async Task Parse_EmptyTrigger_ReturnsLegacySnapshot()
    {
        var result = ControlJobParser.Parse(string.Empty);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.LegacySnapshot);
        await Assert.That(result.Error).IsNull();
    }

    [Test]
    public async Task Parse_ViewsDump_ReturnsTrimmedViewNames()
    {
        const string json = """
                            {
                              "command": "views-dump",
                              "views": ["Level 1 Plan", "  Level 2 Plan  "]
                            }
                            """;

        var result = ControlJobParser.Parse(json);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ViewsDump);
        await Assert.That(result.Views).IsEquivalentTo(new[]
        {
            "Level 1 Plan",
            "Level 2 Plan"
        });
    }

    [Test]
    public async Task Parse_UnknownCommand_ReturnsReadableError()
    {
        var result = ControlJobParser.Parse("{\"command\":\"explode\",\"views\":[]}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("Unknown command: explode");

        var response = ViewDumpTextFormatter.Format(new ViewDumpReport
        {
            Command = "invalid",
            Status = "error",
            Message = result.Error
        });
        await Assert.That(response).Contains("status: error");
        await Assert.That(response).Contains("Unknown command: explode");
    }

    [Test]
    public async Task Parse_ViewsDumpWithoutViews_ReturnsReadableError()
    {
        var result = ControlJobParser.Parse("{\"command\":\"views-dump\",\"views\":[]}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("non-empty views list");
    }

    [Test]
    public async Task Parse_DocumentInfo_ReturnsCommand()
    {
        var result = ControlJobParser.Parse("{\"command\":\"document-info\"}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.DocumentInfo);
        await Assert.That(result.Command).IsEqualTo("document-info");
    }

    [Test]
    public async Task Parse_Address_ReturnsNormalizedTargets()
    {
        var result = ControlJobParser.Parse(
            "{\"command\":\"document-info\",\"targetDocument\":\" Sample Model \",\"targetProcessId\":4242}");

        await Assert.That(result.TargetDocument).IsEqualTo("Sample Model");
        await Assert.That(result.TargetProcessId).IsEqualTo(4242);
    }

    [Test]
    [Arguments("Sample Model", "/Models/Other.rvt", "Sample", true)]
    [Arguments("Other", "/Models/Sample Model.rvt", "Model.rvt", true)]
    [Arguments("Sample Model", "/Models/Other.rvt", "sAmPlE", true)]
    [Arguments(null, "/Models/Sample Model.RVT", "mOdEl.rvt", true)]
    [Arguments("Other", "/Sample/Other.rvt", "Sample", false)]
    [Arguments("Other", "/Models/Other.rvt", "Missing", false)]
    [Arguments("Unsaved Model", "", "Unsaved", true)]
    [Arguments(null, null, "Sample", false)]
    public async Task JobTargetMatcher_MatchesDocument_UsesTitleOrFileName(
        string? title, string? path, string reference, bool expected)
    {
        await Assert.That(JobTargetMatcher.MatchesDocument(title, path, reference)).IsEqualTo(expected);
        var job = ControlJobParseResult.FromContract(new ControlJobContract
        {
            Command = "document-info",
            TargetDocument = reference,
            TargetProcessId = 42
        });
        await Assert.That(JobTargetMatcher.Matches(job, title, path, 42)).IsEqualTo(expected);
        await Assert.That(JobTargetMatcher.Matches(job, title, path, 43)).IsFalse();
    }

    [Test]
    public async Task JobTargetMatcher_RejectsForeignDocumentAndAcceptsUnaddressedJob()
    {
        var foreign = ControlJobParser.Parse(
            "{\"command\":\"document-info\",\"targetDocument\":\"Sample Model\"}");
        var unaddressed = ControlJobParser.Parse("{\"command\":\"document-info\"}");

        await Assert.That(JobTargetMatcher.Matches(foreign, "Test Model", @"C:\\Models\\Test Model.rvt", 42)).IsFalse();
        await Assert.That(JobTargetMatcher.Matches(unaddressed, "Test Model", @"C:\\Models\\Test Model.rvt", 42)).IsTrue();
    }

    [Test]
    public async Task JobTargetMatcher_ForeignDocument_DoesNotClaimTriggerFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RevitModelMcp-{Guid.NewGuid():N}");
        var triggerPath = Path.Combine(directory, "trigger.txt");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(triggerPath, "{\"command\":\"document-info\",\"targetDocument\":\"Sample Model\"}");
            var job = ControlJobParser.Parse(File.ReadAllText(triggerPath));

            var claimed = JobTargetMatcher.TryClaim(
                triggerPath, job, "Test Model", @"C:\\Models\\Test Model.rvt", 42);

            await Assert.That(claimed).IsFalse();
            await Assert.That(File.Exists(triggerPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Parse_Ping_ReturnsDocumentIndependentCommand()
    {
        var result = ControlJobParser.Parse("{\"command\":\"ping\"}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Ping);
        await Assert.That(result.Command).IsEqualTo("ping");
    }

    [Test]
    public async Task Parse_ListViews_ReturnsOptionalFilters()
    {
        const string json = """
                            {
                              "command": "list-views",
                              "viewType": " FloorPlan ",
                              "nameContains": " Plan "
                            }
                            """;

        var result = ControlJobParser.Parse(json);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ListViews);
        await Assert.That(result.ViewType).IsEqualTo("FloorPlan");
        await Assert.That(result.NameContains).IsEqualTo("Plan");
    }

    [Test]
    public async Task Parse_ViewSummary_ReturnsViewName()
    {
        var result = ControlJobParser.Parse("{\"command\":\"view-summary\",\"view\":\"Level 1 Plan\"}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ViewSummary);
        await Assert.That(result.View).IsEqualTo("Level 1 Plan");
    }

    [Test]
    public async Task Parse_ViewElements_ReturnsCategoriesAndPage()
    {
        const string json = """
                            {
                              "command": "view-elements",
                              "view": "Level 1 Plan",
                              "categories": ["Walls", " Doors ", "walls"],
                              "offset": 25,
                              "limit": 10
                            }
                            """;

        var result = ControlJobParser.Parse(json);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ViewElements);
        await Assert.That(result.View).IsEqualTo("Level 1 Plan");
        await Assert.That(result.Categories).IsEquivalentTo(new[] { "Walls", "Doors" });
        await Assert.That(result.Offset).IsEqualTo(25);
        await Assert.That(result.Limit).IsEqualTo(10);
    }

    [Test]
    public async Task Parse_ElementDetails_ReturnsId()
    {
        var result = ControlJobParser.Parse("{\"command\":\"element-details\",\"id\":11327511}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ElementDetails);
        await Assert.That(result.ElementId).IsEqualTo(11327511L);
    }

    [Test]
    public async Task Parse_ViewWarnings_ReturnsViewName()
    {
        var result = ControlJobParser.Parse("{\"command\":\"view-warnings\",\"view\":\"Level 1 Plan\"}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ViewWarnings);
        await Assert.That(result.View).IsEqualTo("Level 1 Plan");
    }

    [Test]
    public async Task Parse_ExportView_ReturnsDefaults()
    {
        var result = ControlJobParser.Parse("{\"command\":\"export-view\",\"view\":\"Level 1 Plan\"}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ExportView);
        await Assert.That(result.View).IsEqualTo("Level 1 Plan");
        await Assert.That(result.PixelSize).IsEqualTo(1600);
        await Assert.That(result.ZoomToFit).IsTrue();
    }

    [Test]
    public async Task Parse_ExportView_ReturnsExplicitParameters()
    {
        var result = ControlJobParser.Parse(
            "{\"command\":\"export-view\",\"view\":\" 11327511 \",\"pixelSize\":4000,\"zoomToFit\":false}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ExportView);
        await Assert.That(result.View).IsEqualTo("11327511");
        await Assert.That(result.PixelSize).IsEqualTo(4000);
        await Assert.That(result.ZoomToFit).IsFalse();
    }

    [Test]
    public async Task Parse_ExportViewWithInvalidParameters_ReturnsReadableError()
    {
        var missingView = ControlJobParser.Parse("{\"command\":\"export-view\",\"pixelSize\":1600}");
        var oversized = ControlJobParser.Parse(
            "{\"command\":\"export-view\",\"view\":\"Level 1 Plan\",\"pixelSize\":4001}");

        await Assert.That(missingView.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(missingView.Error).Contains("requires the view field");
        await Assert.That(oversized.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(oversized.Error).Contains("between 1 and 4000");
    }

    [Test]
    public async Task ViewReferenceMatcher_UnknownView_ReturnsNull()
    {
        var views = new[]
        {
            new TestView(42, "Level 1 Plan"),
            new TestView(84, "Level 2 Plan"),
            new TestView(100, "84")
        };

        var missing = ViewReferenceMatcher.Find(views, "Missing View", view => view.Id, view => view.Name);
        var byId = ViewReferenceMatcher.Find(views, "42", view => view.Id, view => view.Name);
        var numericName = ViewReferenceMatcher.Find(views, "84", view => view.Id, view => view.Name);

        await Assert.That(missing).IsNull();
        await Assert.That(byId?.Name).IsEqualTo("Level 1 Plan");
        await Assert.That(numericName?.Id).IsEqualTo(100);
    }

    [Test]
    public async Task ViewNotFound_ReturnsListViewsHint()
    {
        var response = CommandResponse<object>.ViewNotFound("export-view", "Missing View", 12);

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Message).Contains("View 'Missing View' was not found");
        await Assert.That(response.Message).Contains("list-views");
    }

    [Test]
    public async Task Parse_ViewElementsWithInvalidPage_ReturnsReadableError()
    {
        var result = ControlJobParser.Parse(
            "{\"command\":\"view-elements\",\"view\":\"Level 1 Plan\",\"offset\":-1,\"limit\":0}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("offset");
    }

    [Test]
    public async Task Parse_ElementDetailsWithInvalidId_ReturnsReadableError()
    {
        var result = ControlJobParser.Parse("{\"command\":\"element-details\",\"id\":0}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("positive id");
    }

    [Test]
    public async Task TriggerWatcher_FileAppears_RequestsExternalEvent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RevitModelMcp-{Guid.NewGuid():N}");
        var triggerPath = Path.Combine(directory, "trigger.txt");
        var temporaryPath = Path.Combine(directory, "mcp.tmp");
        using var requested = new ManualResetEventSlim();
        try
        {
            using (var watcher = new TriggerFileWatcher(
                       triggerPath,
                       requested.Set,
                       _ => { },
                       TimeSpan.FromSeconds(10)))
            {
                watcher.Start();
                File.WriteAllText(temporaryPath, "{\"command\":\"ping\"}");
                File.Move(temporaryPath, triggerPath);

                await Assert.That(requested.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            }
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
    public async Task ExternalEventQueue_RequestDuringExecution_RaisesAgain()
    {
        var raises = 0;
        var executions = 0;
        var queue = new ExternalEventRequestQueue(() => raises++);
        queue.Request();

        queue.Execute(
            () =>
            {
                executions++;
                queue.Request();
            },
            _ => { });
        queue.Execute(() => executions++, _ => { });

        await Assert.That(raises).IsEqualTo(2);
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task ExternalEventQueue_HandlerFails_WritesErrorResponse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RevitModelMcp-{Guid.NewGuid():N}");
        var responsePath = Path.Combine(directory, "response_error.json");
        Directory.CreateDirectory(directory);
        try
        {
            var queue = new ExternalEventRequestQueue(() => { });
            queue.Request();

            queue.Execute(
                () => throw new InvalidOperationException("boom"),
                exception => File.WriteAllText(responsePath, exception.Message));

            await Assert.That(File.Exists(responsePath)).IsTrue();
            await Assert.That(File.ReadAllText(responsePath)).IsEqualTo("boom");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed record TestView(long Id, string Name);
}
