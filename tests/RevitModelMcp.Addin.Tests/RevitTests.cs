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
            ParameterFilters = [new ParameterFilterContract
            {
                Parameter = parameterName,
                Operator = "empty"
            }]
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

    [Test]
    [Arguments("group", true, false)]
    [Arguments("group", false, true)]
    [Arguments("group", false, false)]
    [Arguments("auto", true, false)]
    [Arguments("auto", false, true)]
    [Arguments("auto", false, false)]
    public async Task CodeExecution_ProtectsBothDocumentsWithoutConstructorTrackingAsync(string mode, bool dryRun, bool fail)
    {
        using var other = Application.NewProjectDocument(UnitSystem.Metric);
        var initialTarget = Model.ProjectInformation.Number;
        var initialOther = other.ProjectInformation.Number;
        var marker = Guid.NewGuid().ToString("N");
        try
        {
            var result = ExecuteCode(new ActionJobContract
            {
                TransactionMode = mode,
                DryRun = dryRun,
                Args = new Dictionary<string, object>
                {
                    ["other"] = other.GetHashCode(),
                    ["marker"] = marker,
                    ["mode"] = mode,
                    ["fail"] = fail
                },
                Code = """
                    var other = ctx.Application.Documents.Cast<Document>().Single(document => document.GetHashCode() == (int)ctx.Args["other"]);
                    var marker = (string)ctx.Args["marker"];
                    void Change(Document target)
                    {
                        for (var index = 0; index < 2; index++)
                        {
                            using var transaction = (Transaction)Activator.CreateInstance(typeof(Transaction), new object[] { target, "Cross-document change " + index });
                            transaction.Start();
                            target.ProjectInformation.Number = marker + index;
                            transaction.Commit();
                        }
                    }
                    if ((string)ctx.Args["mode"] == "auto") ctx.Document.ProjectInformation.Number = marker + "1";
                    else Change(ctx.Document);
                    Change(other);
                    if ((bool)ctx.Args["fail"]) throw new InvalidOperationException("Cross-document failure");
                    return null;
                    """
            });
            await Assert.That(result.CodeError is not null).IsEqualTo(fail);
            var targetReceipt = result.DocumentReceipts!.Single(receipt => receipt.DocumentKey == Model.GetHashCode().ToString("X8"));
            var otherReceipt = result.DocumentReceipts!.Single(receipt => receipt.DocumentKey == other.GetHashCode().ToString("X8"));
            await Assert.That(targetReceipt.DocumentKey == otherReceipt.DocumentKey).IsFalse();
            if (dryRun || fail)
            {
                await Assert.That(Model.ProjectInformation.Number).IsEqualTo(initialTarget);
                await Assert.That(other.ProjectInformation.Number).IsEqualTo(initialOther);
                await Assert.That(targetReceipt.Outcome).IsEqualTo("rolledBack");
                await Assert.That(otherReceipt.Outcome).IsEqualTo("rolledBack");
                await Assert.That(result.RolledBack).IsTrue();
            }
            else
            {
                await Assert.That(Model.ProjectInformation.Number).IsEqualTo(marker + "1");
                await Assert.That(other.ProjectInformation.Number).IsEqualTo(marker + "1");
                await Assert.That(targetReceipt.Outcome).IsEqualTo("committed");
                await Assert.That(otherReceipt.Outcome).IsEqualTo("committed");
                var expectedUndo = ActionSummaryBuilder.BuildGroupName("RMM tests", "Execute code");
                await Assert.That(targetReceipt.UndoName).IsEqualTo(expectedUndo);
                await Assert.That(otherReceipt.UndoName).IsEqualTo(expectedUndo);
                await Assert.That(result.Committed).IsTrue();
            }
        }
        finally
        {
            other.Close(false);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CodeGroup_EditFamilyLoadFamilyUsesConflictOptionsAndHonorsDryRunAsync(bool dryRun)
    {
        var familyName = "CrossDocumentRollback" + Guid.NewGuid().ToString("N");
        var initialDocuments = Application.Documents.Cast<Document>().Select(document => document.GetHashCode()).ToHashSet();
        var initialFamilies = new FilteredElementCollector(Model).OfClass(typeof(Family)).GetElementCount();
        try
        {
            var result = ExecuteCode(new ActionJobContract
            {
                TransactionMode = "group",
                DryRun = dryRun,
                Args = new Dictionary<string, object> { ["name"] = familyName },
                Code = """
                    var family = new FilteredElementCollector(ctx.Document).OfClass(typeof(Family)).Cast<Family>()
                        .First(item => item.IsEditable && !item.IsInPlace);
                    var edited = ctx.Document.EditFamily(family);
                    using (var transaction = new Transaction(edited, "Rename edited family"))
                    {
                        transaction.Start();
                        edited.OwnerFamily.Name = (string)ctx.Args["name"];
                        transaction.Commit();
                    }
                    var loaded = edited.LoadFamily(ctx.Document, new Nice3point.Revit.Toolkit.Options.FamilyLoadOptions());
                    return loaded.Name;
                    """
            });
            await Assert.That(result.CodeError).IsNull();
            await Assert.That(result.ReturnValue).IsEqualTo(familyName);
            var loadedFamilyExists = new FilteredElementCollector(Model).OfClass(typeof(Family)).Cast<Family>()
                .Any(family => family.Name == familyName);
            if (dryRun)
            {
                await Assert.That(new FilteredElementCollector(Model).OfClass(typeof(Family)).GetElementCount()).IsEqualTo(initialFamilies);
                await Assert.That(loadedFamilyExists).IsFalse();
                await Assert.That(Application.Documents.Cast<Document>().Select(document => document.GetHashCode()).ToHashSet()
                    .SetEquals(initialDocuments)).IsTrue();
                await Assert.That(result.DocumentReceipts!.Any(receipt => receipt.OpenedByCall && receipt.Outcome == "rolledBack")).IsTrue();
                await Assert.That(result.RolledBack).IsTrue();
            }
            else
            {
                await Assert.That(loadedFamilyExists).IsTrue();
                await Assert.That(result.DocumentReceipts!.Single(receipt => receipt.DocumentKey == Model.GetHashCode().ToString("X8"))
                    .Outcome).IsEqualTo("committed");
                await Assert.That(result.DocumentReceipts!.Any(receipt => receipt.OpenedByCall && receipt.Outcome == "leftOpen")).IsTrue();
                await Assert.That(result.Committed).IsTrue();
            }
        }
        finally
        {
            foreach (var opened in Application.Documents.Cast<Document>()
                         .Where(document => !document.IsLinked && !initialDocuments.Contains(document.GetHashCode())).ToList())
                opened.Close(false);
        }
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task CodeRollback_ClosesDocumentsCreatedDuringTheCallAsync(bool dryRun, bool fail)
    {
        var initialDocuments = Application.Documents.Cast<Document>().Select(document => document.GetHashCode()).ToHashSet();
        var result = ExecuteCode(new ActionJobContract
        {
            TransactionMode = "group",
            DryRun = dryRun,
            Args = new Dictionary<string, object> { ["fail"] = fail },
            Code = """
                var created = ctx.Application.NewProjectDocument(UnitSystem.Metric);
                using (var transaction = new Transaction(created, "Create preview level"))
                {
                    transaction.Start();
                    Level.Create(created, 10);
                    transaction.Commit();
                }
                if ((bool)ctx.Args["fail"]) throw new InvalidOperationException("New document failure");
                return null;
                """
        });
        await Assert.That(result.CodeError is not null).IsEqualTo(fail);
        await Assert.That(Application.Documents.Cast<Document>().Select(document => document.GetHashCode()).ToHashSet()
            .SetEquals(initialDocuments)).IsTrue();
        await Assert.That(result.DocumentReceipts!.Single(receipt => receipt.OpenedByCall).Outcome).IsEqualTo("rolledBack");
        await Assert.That(result.RolledBack).IsTrue();
    }

    [Test]
    [Arguments("auto", true)]
    [Arguments("auto", false)]
    [Arguments("group", true)]
    [Arguments("group", false)]
    public async Task CodeExecution_RefusesBeforeRunningWhenAnotherDocumentHasAnOpenTransactionAsync(string mode, bool dryRun)
    {
        using var other = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            using var callerTransaction = new Transaction(other, "Caller-owned transaction");
            callerTransaction.Start();
            other.ProjectInformation.Number = "Caller-owned change";
            try
            {
                var result = ExecuteCode(new ActionJobContract
                {
                    TransactionMode = mode,
                    DryRun = dryRun,
                    Args = new Dictionary<string, object> { ["other"] = other.GetHashCode() },
                    Code = """
                        ctx.Log("Script ran");
                        var other = ctx.Application.Documents.Cast<Document>().Single(document => document.GetHashCode() == (int)ctx.Args["other"]);
                        other.ProjectInformation.Number = "Unexpected script change";
                        return null;
                        """
                });
                await Assert.That(result.CodeError!).Contains("The script did not run");
                await Assert.That(result.CodeError!).Contains(other.Title);
                await Assert.That(result.CodeError!).Contains("open transaction or edit mode");
                await Assert.That(result.CodeError!).Contains("Finish the open edit or transaction");
                await Assert.That(result.Log!.Count).IsEqualTo(0);
                await Assert.That(other.ProjectInformation.Number).IsEqualTo("Caller-owned change");
                await Assert.That(callerTransaction.GetStatus()).IsEqualTo(TransactionStatus.Started);
            }
            finally
            {
                callerTransaction.RollBack();
            }
        }
        finally
        {
            other.Close(false);
        }
    }

    private ActionResultData ExecuteCode(ActionJobContract action)
    {
        var failures = Activator.CreateInstance(Type("Control.ActionCommandExecutor+ActionFailures"), true);
        return Invoke<ActionResultData>(Type("Control.CodeExecution"), null, "Execute",
            UiApplication, Model, null, action, failures, "RMM tests", null, false);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewDocumentActivationFailureAccountsForReplacementDocumentAsync(bool retainActiveDocument)
    {
        var originalActive = UiApplication.ActiveUIDocument?.Document;
        // Revit cannot close its active document or restore a session with no active document through the API.
        if (originalActive is null || !File.Exists(originalActive.PathName))
            Skip.Test("Activation cleanup requires an existing saved active document for restoring the UI session.");
        var template = Environment.GetEnvironmentVariable("RMM_TEST_PROJECT_TEMPLATE")
            ?? Application.DefaultProjectTemplate;
        if (!File.Exists(template)) Skip.Test("Set RMM_TEST_PROJECT_TEMPLATE to an existing project template.");
        var before = Application.Documents.Cast<Document>().ToList();
        var path = Path.Combine(_directory!, "activation-failure.rvt");
        Document? replacement = null;
        string? identity = null;
        var injected = false;
        try
        {
            Exception? failure = null;
            try
            {
                RevitModelMcp.Control.DocumentActions.Execute(UiApplication, "new-document", new ActionJobContract
                {
                    Kind = "project",
                    Template = template,
                    SaveAs = path,
                    Activate = true
                }, (application, savedPath) =>
                {
                    // Force a replacement wrapper; normal activation can reuse the background document.
                    var background = Application.Documents.Cast<Document>().Single(document => document.PathName == savedPath);
                    if (!background.Close(false)) throw new InvalidOperationException("Could not prepare replacement activation.");
                    replacement = application.OpenAndActivateDocument(savedPath).Document;
                    identity = $"{replacement.Title} [{replacement.GetHashCode():X}]";
                    if (!retainActiveDocument) application.OpenAndActivateDocument(originalActive!.PathName);
                    injected = true;
                    throw new InvalidOperationException("Injected activation failure after opening the UI document.");
                });
            }
            catch (InvalidOperationException exception)
            {
                failure = exception;
            }
            await Assert.That(injected).IsTrue();
            await Assert.That(failure is not null).IsTrue();
            await Assert.That(failure!.Message.Contains("Injected activation failure")).IsTrue();
            await Assert.That(failure.Message.Contains(retainActiveDocument
                ? $"Retained document '{identity}'" : $"Closed '{identity}'")).IsTrue();
            await Assert.That(replacement!.IsValidObject).IsEqualTo(retainActiveDocument);
            await AssertInventoryAsync(retainActiveDocument ? [.. before, replacement!] : before);
        }
        finally
        {
            if (replacement is { IsValidObject: true })
            {
                UiApplication.OpenAndActivateDocument(originalActive!.PathName);
                replacement.Close(false);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewDocumentSaveFailurePreservesExistingDocumentsAsync(bool refuseClosure)
    {
        var template = Environment.GetEnvironmentVariable("RMM_TEST_PROJECT_TEMPLATE")
            ?? Application.DefaultProjectTemplate;
        if (!File.Exists(template)) Skip.Test("Set RMM_TEST_PROJECT_TEMPLATE to an existing project template.");
        var before = Application.Documents.Cast<Document>().ToList();
        Document? created = null;
        string? identity = null;
        var closureAttempted = false;
        void Created(object? sender, DocumentCreatedEventArgs arguments)
        {
            created = arguments.Document;
            identity = $"{created.Title} [{created.GetHashCode():X}]";
        }
        void Closing(object? sender, DocumentClosingEventArgs arguments)
        {
            if (!arguments.Document.Equals(created)) return;
            closureAttempted = true;
            if (refuseClosure) arguments.Cancel();
        }
        Application.DocumentCreated += Created;
        Application.DocumentClosing += Closing;
        try
        {
            Exception? failure = null;
            try
            {
                Invoke<ActionResultData>(Type("Control.DocumentActions"), null, "Execute",
                    UiApplication, "new-document", new ActionJobContract
                    {
                        Kind = "project",
                        Template = template,
                        Activate = false,
                        SaveAs = Path.Combine(_directory!, "missing", "project.rvt")
                    });
            }
            catch (InvalidOperationException exception)
            {
                failure = exception;
            }
            await Assert.That(failure is not null).IsTrue();
            await Assert.That(created is not null).IsTrue();
            await Assert.That(closureAttempted).IsTrue();
            await Assert.That(created!.IsValidObject).IsEqualTo(refuseClosure);
            await Assert.That(failure!.Message.Contains(refuseClosure
                ? $"Retained document '{identity}'" : $"Closed '{identity}'")).IsTrue();
            await AssertInventoryAsync(refuseClosure ? [.. before, created!] : before);
        }
        finally
        {
            Application.DocumentCreated -= Created;
            Application.DocumentClosing -= Closing;
            if (created is { IsValidObject: true }) created.Close(false);
        }
    }

    [Test]
    [Arguments("edit")]
    [Arguments("load")]
    [Arguments("closure")]
    public async Task FamilyEditFailureReportsDocumentClosureAsync(string failureStage)
    {
        var family = EditableFamily();
        var before = Application.Documents.Cast<Document>().ToList();
        Document? edited = null;
        string? identity = null;
        var loadAttempted = false;
        void Loading(object? sender, FamilyLoadingIntoDocumentEventArgs arguments)
        {
            if (!arguments.Document.Equals(Model)) return;
            loadAttempted = true;
            arguments.Cancel();
        }
        void Closing(object? sender, DocumentClosingEventArgs arguments)
        {
            if (before.Any(document => document.Equals(arguments.Document))) return;
            edited = arguments.Document;
            identity = $"{edited.Title} [{edited.GetHashCode():X}]";
            if (failureStage == "closure") arguments.Cancel();
        }
        Application.FamilyLoadingIntoDocument += Loading;
        Application.DocumentClosing += Closing;
        try
        {
            var result = EditFamilies(new ActionJobContract
            {
                Families = [family.Name],
                StopOnError = true,
                Operations = failureStage == "load"
                    ? [new FamilyEditOperationContract
                    {
                        Op = "set_shared",
                        Shared = family.get_Parameter(BuiltInParameter.FAMILY_SHARED).AsInteger() != 1
                    }]
                    : [new FamilyEditOperationContract
                    {
                        Op = "add_shared_parameters",
                        SharedParameterFile = Path.Combine(_directory!, "missing-parameters.txt"),
                        Parameters = []
                    }]
            });
            var receipt = result.Families.Single();
            await Assert.That(receipt.Status).IsEqualTo("failed");
            await Assert.That(receipt.Reason is not null).IsTrue();
            await Assert.That(result.FailedFamily).IsEqualTo(family.Name);
            await Assert.That(result.RolledBack).IsTrue();
            await Assert.That(result.Committed).IsFalse();
            await Assert.That(loadAttempted).IsEqualTo(failureStage == "load");
            await Assert.That(edited is not null).IsTrue();
            await Assert.That(receipt.Closure is not null).IsTrue();
            await Assert.That(receipt.Closure!.Identity).IsEqualTo(identity);
            await Assert.That(receipt.Closure.Closed).IsEqualTo(failureStage != "closure");
            await Assert.That(edited!.IsValidObject).IsEqualTo(failureStage == "closure");
            await Assert.That(result.HasRetainedDocument).IsEqualTo(failureStage == "closure");
            await AssertInventoryAsync(failureStage == "closure" ? [.. before, edited!] : before);
        }
        finally
        {
            Application.FamilyLoadingIntoDocument -= Loading;
            Application.DocumentClosing -= Closing;
            foreach (var document in Application.Documents.Cast<Document>().ToList())
                if (!before.Any(existing => existing.Equals(document))) document.Close(false);
        }
    }

    [Test]
    public async Task FamilyEditKeepsPreExistingFamilyDocumentOpenAsync()
    {
        var family = EditableFamily();
        var existing = Model.EditFamily(family);
        try
        {
            var before = Application.Documents.Cast<Document>().ToList();
            var result = EditFamilies(new ActionJobContract
            {
                Families = [family.Name],
                StopOnError = true,
                Operations = [new FamilyEditOperationContract
                {
                    Op = "add_shared_parameters",
                    SharedParameterFile = Path.Combine(_directory!, "missing-parameters.txt"),
                    Parameters = []
                }]
            });
            await Assert.That(result.Families.Single().Status).IsEqualTo("failed");
            await Assert.That(result.Families.Single().Closure is null).IsTrue();
            await Assert.That(existing.IsValidObject).IsTrue();
            await AssertInventoryAsync(before);
        }
        finally
        {
            if (existing.IsValidObject) existing.Close(false);
        }
    }

    private Family EditableFamily() =>
        new FilteredElementCollector(Model).OfClass(typeof(Family)).Cast<Family>()
            .First(family => family.IsEditable && !family.IsInPlace &&
                family.get_Parameter(BuiltInParameter.FAMILY_SHARED) is not null);

    private FamilyEditData EditFamilies(ActionJobContract job)
    {
        var failures = Activator.CreateInstance(Type("Control.ActionCommandExecutor+ActionFailures"), true);
        return Invoke<FamilyEditData>(Type("Control.FamilyEditor"), null, "Execute",
            Model, job, failures, UiApplication, "RMM tests");
    }

    private static async Task AssertInventoryAsync(List<Document> expected)
    {
        var actual = Application.Documents.Cast<Document>().ToList();
        await Assert.That(actual.Count).IsEqualTo(expected.Count);
        await Assert.That(expected.All(document => document.IsValidObject &&
            actual.Any(candidate => candidate.Equals(document)))).IsTrue();
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
