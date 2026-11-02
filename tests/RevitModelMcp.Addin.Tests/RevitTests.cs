using System.Reflection;
using System.Runtime.ExceptionServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Nice3point.TUnit.Revit;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Addin.Tests;

public sealed class RevitTests : RevitApiUiTest
{
    private static Autodesk.Revit.ApplicationServices.Application Application => UiApplication.Application;
    private Document? _document;
    private string? _directory;
    private Document Model => _document ?? throw new InvalidOperationException("The model is not open.");
    private static Assembly Addin => typeof(RevitModelMcp.Application).Assembly;

    [Before(Test)]
    public void OpenModel()
    {
        var source = Environment.GetEnvironmentVariable("RMM_TEST_MODEL")
            ?? @"C:\Projects\rmm-test\2027\Snowdon Towers Sample Architectural.rvt";
        if (!File.Exists(source)) Skip.Test($"Set RMM_TEST_MODEL to an existing Revit model: {source}");
        _directory = Path.Combine(Path.GetTempPath(), "rmm-addin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var copy = Path.Combine(_directory, Path.GetFileName(source));
        File.Copy(source, copy);
        File.SetAttributes(copy, FileAttributes.Normal);
        using var options = new OpenOptions();
        options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
        options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
        _document = Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(copy), options);
        SkippedReadDiagnostics.Current = new SkippedReadDiagnostics();
    }

    [After(Test)]
    public void CloseModel()
    {
        try { _document?.Close(false); }
        finally
        {
            SkippedReadDiagnostics.Current = null;
            if (_directory is not null) Directory.Delete(_directory, true);
        }
    }

    [Test]
    public async Task CatalogLimitAndNameFilterAsync()
    {
        var result = Read<CatalogData>("CatalogReader", "Read", Model, "categories", null, 1);
        await Assert.That(result.Total > 1).IsTrue();
        await Assert.That(result.Items.Count).IsEqualTo(1);
        await Assert.That(result.HasMore).IsTrue();
        var filtered = Read<CatalogData>("CatalogReader", "Read", Model, "categories", result.Items[0].Name, 100);
        await Assert.That(filtered.Items.All(item => item.Name.Contains(result.Items[0].Name))).IsTrue();
    }

    [Test]
    public async Task AggregateMatchesCollectorAsync()
    {
        var result = Read<AggregateElementsData>("ElementQueryReader", "ReadAggregate", Model,
            Parse("""{"command":"aggregate-elements","categories":["OST_Walls"],"groupBy":["type"]}"""));
        await Assert.That(result.MatchedElements).IsEqualTo(WallElements().Count);
        await Assert.That(result.Groups.Sum(group => group.Count)).IsEqualTo(result.MatchedElements);
    }

    [Test]
    public async Task QueryPagesHaveNoOverlapAsync()
    {
        var first = Query(0, 2);
        var second = Query(2, 2);
        await Assert.That(first.Total).IsEqualTo(WallElements().Count);
        await Assert.That(first.Elements.Count).IsEqualTo(2);
        await Assert.That(second.Elements.Count).IsEqualTo(2);
        await Assert.That(first.HasMore).IsTrue();
        await Assert.That(first.Elements.Select(item => item.Id).Intersect(second.Elements.Select(item => item.Id)).Any()).IsFalse();
        await Assert.That(first.Elements.Last().Id < second.Elements.First().Id).IsTrue();
    }

    [Test]
    public async Task QueryPastEndIsEmptyAsync()
    {
        var result = Query(WallElements().Count, 2);
        await Assert.That(result.Elements.Count).IsEqualTo(0);
        await Assert.That(result.HasMore).IsFalse();
    }

    [Test]
    public async Task QueryParameterFilterMatchesApiAsync()
    {
        var parameter = WallElements()[0].get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        var parameterName = parameter.Definition.Name;
        var job = ControlJobParseResult.FromContract(new ControlJobContract
        {
            Command = "query-elements",
            Categories = ["OST_Walls"],
            Limit = 10000,
            ParameterFilters = [new ParameterFilterContract { Parameter = parameterName, Operator = "empty" }]
        });
        if (job.Error is not null) throw new ArgumentException(job.Error);
        var result = Read<QueryElementsData>("ElementQueryReader", "ReadQuery", Model, job);
        var expected = WallElements().Count(wall => wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is { HasValue: false });
        await Assert.That(result.Total).IsEqualTo(expected);
    }

    [Test]
    public async Task MissingElementReportsSkippedReadAsync()
    {
        SkippedReadDiagnostics.Current = new SkippedReadDiagnostics();
        var missing = Read<Element?>("ElementQueryReader", "ReadElement", Model, ElementId.InvalidElementId);
        await Assert.That(missing is null).IsTrue();
        await Assert.That(SkippedReadDiagnostics.Current!.Items.Count).IsEqualTo(1);
        await Assert.That(SkippedReadDiagnostics.Current.Items[0].Reason).IsEqualTo("Element is unavailable.");
        var result = Query(0, 2);
        await Assert.That(result.Elements.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ElementDetailsMatchInstanceAndTypeAsync()
    {
        var wall = WallElements()[0];
        var reader = Activator.CreateInstance(Type("Capture.ViewElementReader"), Model, new HashSet<long>());
        var details = Invoke<ElementDetailsData>(Type("Capture.ViewElementReader"), reader, "ReadDetails", wall);
        await Assert.That(details.Element.Id).IsEqualTo(Id(wall.Id));
        await Assert.That(details.TypeElement!.Id).IsEqualTo(Id(wall.GetTypeId()));
        await Assert.That(details.Parameters.Count > 0).IsTrue();
    }

    [Test]
    public async Task WarningGroupsMatchApiAsync()
    {
        var result = Read<ModelWarningsData>("ModelWarningReader", "Read", Model, null, true);
        await Assert.That(result.TotalWarnings).IsEqualTo(Model.GetWarnings().Count);
        await Assert.That(result.Groups.Sum(group => group.Count)).IsEqualTo(result.TotalWarnings);
        await Assert.That(result.Groups.All(group => group.Elements is not null)).IsTrue();
    }

    [Test]
    public async Task HealthCountsMatchApiAsync()
    {
        var health = Read<ModelHealthData>("ModelHealthReader", "Read", Model, new ControlJobContract(), true);
        var counts = health.Counts;
        await Assert.That(health.RevitVersion).IsEqualTo(Application.VersionNumber);
        await Assert.That(counts["elements"]).IsEqualTo((int?)new FilteredElementCollector(Model).WhereElementIsNotElementType().GetElementCount());
        await Assert.That(counts["warnings"]).IsEqualTo((int?)Model.GetWarnings().Count);
        await Assert.That(counts["levels"]).IsEqualTo((int?)new FilteredElementCollector(Model).OfClass(typeof(Level)).GetElementCount());
    }

    [Test]
    public async Task FillCheckAccountsForEveryWallAsync()
    {
        var result = Read<ParameterFillData>("ParameterFillReader", "Read", Model, new ControlJobContract
        {
            Categories = ["OST_Walls"],
            Parameters = [WallElements()[0].get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Definition.Name],
            SampleLimit = 2
        });
        var item = result.Parameters.Single();
        await Assert.That(item.Filled + item.Empty + item.Missing).IsEqualTo(WallElements().Count);
        await Assert.That(item.EmptySampleIds.Count <= 2 && item.MissingSampleIds.Count <= 2).IsTrue();
    }

    [Test]
    [Arguments("summary")]
    [Arguments("full")]
    public async Task FamilyAuditDetailAsync(string detail)
    {
        var family = new FilteredElementCollector(Model).OfClass(typeof(Family)).Cast<Family>()
            .First(item => item.IsEditable && !item.IsInPlace);
        var result = Read<FamilyAuditData>("FamilyAuditReader", "Read", Model, new List<string> { family.Name }, detail);
        var audited = result.Families.Single();
        await Assert.That(audited.Status).IsEqualTo("audited");
        await Assert.That(audited.Name).IsEqualTo(family.Name);
        await Assert.That(detail == "full" ? audited.Parameters is not null : audited.Parameters is null && audited.ParameterCount.HasValue).IsTrue();
    }

    [Test]
    public async Task ApiLookupUsesInstalledRevitAssemblyAsync()
    {
        var result = ApiLookup.Lookup([typeof(Document).Assembly], new ApiLookupRequest("Autodesk.Revit.DB.Document", "type"), assembly =>
        {
            var path = Path.ChangeExtension(assembly.Location, ".xml");
            return File.Exists(path) ? path : null;
        });
        await Assert.That(result.Types.Single().FullName).IsEqualTo("Autodesk.Revit.DB.Document");
        await Assert.That(result.AssemblyVersions.Single().Version).IsEqualTo(typeof(Document).Assembly.GetName().Version!.ToString());
    }

    [Test]
    public async Task ListViewsMatchesNonTemplateViewsAsync()
    {
        var result = Read<ViewListData>("ReadCommandReader", "ReadViews", Model, null, null, null);
        var expected = new FilteredElementCollector(Model).OfClass(typeof(View)).Cast<View>().Count(view => !view.IsTemplate);
        await Assert.That(result.Views.Count).IsEqualTo(expected);
        await Assert.That(result.Processed).IsEqualTo(result.Total);
    }

    [Test]
    public async Task ReadersRecoverAfterAnotherDocumentClosesAsync()
    {
        var before = Query(0, 2);
        using (var other = Application.NewProjectDocument(UnitSystem.Metric))
        {
            var empty = Read<CatalogData>("CatalogReader", "Read", other, "levels", null, 100);
            await Assert.That(empty.Items.Count <= empty.Total).IsTrue();
            other.Close(false);
        }
        var after = Query(0, 2);
        await Assert.That(after.Elements.Select(item => item.Id).SequenceEqual(before.Elements.Select(item => item.Id))).IsTrue();
    }

    [Test]
    [Arguments("set-parameter")]
    [Arguments("move")]
    [Arguments("copy")]
    [Arguments("delete")]
    public async Task ActionCommitsOneNamedEntryAndRollsBackAsync(string command)
    {
        using var rollback = new TransactionGroup(Model, "RMM test rollback");
        rollback.Start();
        ElementId target;
        using (var seed = new Transaction(Model, "RMM test seed"))
        {
            seed.Start();
            var level = new FilteredElementCollector(Model).OfClass(typeof(Level)).Cast<Level>().First();
            var wallType = new FilteredElementCollector(Model).OfClass(typeof(WallType)).Cast<WallType>()
                .First(type => type.Kind == WallKind.Basic);
            target = Wall.Create(Model, Line.CreateBound(new XYZ(10000, 10000, level.Elevation),
                new XYZ(10010, 10000, level.Elevation)), wallType.Id, level.Id, 10, 0, false, false).Id;
            WallUtils.DisallowWallJoinAtEnd((Wall)Model.GetElement(target), 0);
            WallUtils.DisallowWallJoinAtEnd((Wall)Model.GetElement(target), 1);
            seed.Commit();
        }
        var original = ((LocationCurve)Model.GetElement(target).Location).Curve.GetEndPoint(0);
        var parameter = Model.GetElement(target).get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        var action = new ActionJobContract
        {
            ElementIds = [Id(target)],
            ElementId = Id(target),
            Parameter = parameter.Definition.Name,
            Value = "RMM regression",
            DxMm = 304.8,
            Count = 1,
            MaxElements = 10
        };
        var commits = new List<string>();
        void Changed(object? sender, DocumentChangedEventArgs arguments)
        {
            if (!arguments.GetDocument().Equals(Model)) return;
            var names = arguments.GetTransactionNames();
            if (arguments.Operation == UndoOperation.TransactionCommitted) commits.AddRange(names);
            if (arguments.Operation is UndoOperation.TransactionRolledBack or UndoOperation.TransactionGroupRolledBack)
                foreach (var name in names) commits.RemoveAll(committed => committed == name);
        }
        Application.DocumentChanged += Changed;
        try
        {
            var failures = Activator.CreateInstance(Type("Control.ActionCommandExecutor+ActionFailures"), true);
            var result = Invoke<ActionResultData>(Type("Control.ActionCommandExecutor"), null, "ExecuteStep",
                Model, null, command, action, failures, "RMM tests", false, true);
            var expectedSummary = command switch
            {
                "set-parameter" => $"Set parameter '{action.Parameter}' on 1 element in {Model.Title}.",
                "move" => $"Moved 1 element in {Model.Title}.",
                "copy" => $"Copied 1 element in {Model.Title}.",
                "delete" => $"Deleted 1 element in {Model.Title}.",
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
            await Assert.That(result.Summary).IsEqualTo(expectedSummary);
            await Assert.That(result.UndoName).IsEqualTo(ActionSummaryBuilder.BuildGroupName("RMM tests", result.Summary!));
            Console.WriteLine($"Committed transaction names: {string.Join(", ", commits)}; undo group: {result.UndoName}");
            await Assert.That(commits.Distinct().Single()).IsEqualTo("revit_" + command.Replace('-', '_'));
            if (command == "set-parameter") await Assert.That(parameter.AsString()).IsEqualTo("RMM regression");
            if (command == "move") await Assert.That(((LocationCurve)Model.GetElement(target).Location).Curve.GetEndPoint(0).X - original.X).IsEqualTo(1.0).Within(1e-6);
            if (command == "copy")
            {
                await Assert.That(result.Copies!.Single().Count).IsEqualTo(1);
                await Assert.That(Model.GetElement(CreateId(result.Copies!.Single().Single())) is Wall).IsTrue();
            }
            if (command == "delete") await Assert.That(Model.GetElement(target) is null).IsTrue();
        }
        finally
        {
            Application.DocumentChanged -= Changed;
            rollback.RollBack();
        }
        await Assert.That(Model.GetElement(target) is null).IsTrue();
    }

    private List<Element> WallElements() => new FilteredElementCollector(Model).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().ToElements().ToList();

    private QueryElementsData Query(int offset, int limit) => Read<QueryElementsData>("ElementQueryReader", "ReadQuery", Model,
        ControlJobParseResult.FromContract(new ControlJobContract
        {
            Command = "query-elements",
            Categories = ["OST_Walls"],
            Offset = offset,
            Limit = limit,
            Sort = new QuerySortContract { Field = "id" }
        }));

    private static ControlJobParseResult Parse(string json)
    {
        var result = ControlJobParser.Parse(json);
        if (result.Error is not null) throw new ArgumentException(result.Error);
        return result;
    }

    private static ElementId CreateId(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }

    private static long Id(ElementId id) => long.Parse(id.ToString(), System.Globalization.CultureInfo.InvariantCulture);
    private static Type Type(string name) => Addin.GetType("RevitModelMcp." + name, true)!;
    private static T Read<T>(string type, string method, params object?[] arguments) => Invoke<T>(Type("Capture." + type), null, method, arguments);

    // Readers and executors remain internal; reflection avoids changing the shipped API for tests.
    private static T Invoke<T>(Type type, object? instance, string method, params object?[] arguments)
    {
        var member = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length);
        try { return (T)member.Invoke(instance, arguments)!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
