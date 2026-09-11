using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Formatting;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ControlJobParserTests
{
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
                              "views": ["СПП в ГНС 1-й этаж", "  СПП в ГНС 2-й этаж  "]
                            }
                            """;

        var result = ControlJobParser.Parse(json);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ViewsDump);
        await Assert.That(result.Views).IsEquivalentTo(new[]
        {
            "СПП в ГНС 1-й этаж",
            "СПП в ГНС 2-й этаж"
        });
    }

    [Test]
    public async Task Parse_UnknownCommand_ReturnsReadableError()
    {
        var result = ControlJobParser.Parse("{\"command\":\"explode\",\"views\":[]}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("Неизвестная команда: explode");

        var response = ViewDumpTextFormatter.Format(new ViewDumpReport
        {
            Command = "invalid",
            Status = "error",
            Message = result.Error
        });
        await Assert.That(response).Contains("статус: error");
        await Assert.That(response).Contains("Неизвестная команда: explode");
    }

    [Test]
    public async Task Parse_ViewsDumpWithoutViews_ReturnsReadableError()
    {
        var result = ControlJobParser.Parse("{\"command\":\"views-dump\",\"views\":[]}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("непустой список views");
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
            "{\"command\":\"document-info\",\"targetDocument\":\" QC 0091 \",\"targetProcessId\":4242}");

        await Assert.That(result.TargetDocument).IsEqualTo("QC 0091");
        await Assert.That(result.TargetProcessId).IsEqualTo(4242);
    }

    [Test]
    public async Task JobTargetMatcher_RejectsForeignDocumentAndAcceptsUnaddressedJob()
    {
        var foreign = ControlJobParser.Parse(
            "{\"command\":\"document-info\",\"targetDocument\":\"Customer\"}");
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
            File.WriteAllText(triggerPath, "{\"command\":\"document-info\",\"targetDocument\":\"Customer\"}");
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
                              "nameContains": " ГНС "
                            }
                            """;

        var result = ControlJobParser.Parse(json);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ListViews);
        await Assert.That(result.ViewType).IsEqualTo("FloorPlan");
        await Assert.That(result.NameContains).IsEqualTo("ГНС");
    }

    [Test]
    public async Task Parse_ViewSummary_ReturnsViewName()
    {
        var result = ControlJobParser.Parse("{\"command\":\"view-summary\",\"view\":\"План 1\"}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ViewSummary);
        await Assert.That(result.View).IsEqualTo("План 1");
    }

    [Test]
    public async Task Parse_ViewElements_ReturnsCategoriesAndPage()
    {
        const string json = """
                            {
                              "command": "view-elements",
                              "view": "План 1",
                              "categories": ["Стены", " Двери ", "стены"],
                              "offset": 25,
                              "limit": 10
                            }
                            """;

        var result = ControlJobParser.Parse(json);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ViewElements);
        await Assert.That(result.View).IsEqualTo("План 1");
        await Assert.That(result.Categories).IsEquivalentTo(new[] { "Стены", "Двери" });
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
        var result = ControlJobParser.Parse("{\"command\":\"view-warnings\",\"view\":\"План 1\"}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ViewWarnings);
        await Assert.That(result.View).IsEqualTo("План 1");
    }

    [Test]
    public async Task Parse_ExportView_ReturnsDefaults()
    {
        var result = ControlJobParser.Parse("{\"command\":\"export-view\",\"view\":\"План 1\"}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ExportView);
        await Assert.That(result.View).IsEqualTo("План 1");
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
            "{\"command\":\"export-view\",\"view\":\"План 1\",\"pixelSize\":4001}");

        await Assert.That(missingView.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(missingView.Error).Contains("поле view обязательно");
        await Assert.That(oversized.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(oversized.Error).Contains("от 1 до 4000");
    }

    [Test]
    public async Task ViewReferenceMatcher_UnknownView_ReturnsNull()
    {
        var views = new[]
        {
            new TestView(42, "План 1"),
            new TestView(84, "План 2"),
            new TestView(100, "84")
        };

        var missing = ViewReferenceMatcher.Find(views, "Нет такого вида", view => view.Id, view => view.Name);
        var byId = ViewReferenceMatcher.Find(views, "42", view => view.Id, view => view.Name);
        var numericName = ViewReferenceMatcher.Find(views, "84", view => view.Id, view => view.Name);

        await Assert.That(missing).IsNull();
        await Assert.That(byId?.Name).IsEqualTo("План 1");
        await Assert.That(numericName?.Id).IsEqualTo(100);
    }

    [Test]
    public async Task ViewNotFound_ReturnsListViewsHint()
    {
        var response = CommandResponse<object>.ViewNotFound("export-view", "Нет такого вида", 12);

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Message).Contains("Вид «Нет такого вида» не найден");
        await Assert.That(response.Message).Contains("list-views");
    }

    [Test]
    public async Task Parse_ViewElementsWithInvalidPage_ReturnsReadableError()
    {
        var result = ControlJobParser.Parse(
            "{\"command\":\"view-elements\",\"view\":\"План 1\",\"offset\":-1,\"limit\":0}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("offset");
    }

    [Test]
    public async Task Parse_ElementDetailsWithInvalidId_ReturnsReadableError()
    {
        var result = ControlJobParser.Parse("{\"command\":\"element-details\",\"id\":0}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("положительный id");
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
