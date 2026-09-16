using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal static class ActionCommandExecutor
{
    internal static bool ActionsEnabled => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "allow-write"));

    public static void Execute(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        var stopwatch = Stopwatch.StartNew();
        CommandResponse<ActionResultData> response;
        var dialogsSuppressed = new List<string>();
        var viewOpened = false;
        var failures = new ActionFailures();
        void SuppressDialog(object? sender, DialogBoxShowingEventArgs arguments)
        {
            if (arguments is not TaskDialogShowingEventArgs dialog) return;
            if (dialog.OverrideResult((int)TaskDialogResult.Ok) || dialog.OverrideResult((int)TaskDialogResult.Yes))
                dialogsSuppressed.Add(dialog.Message);
        }
        application.DialogBoxShowing += SuppressDialog;
        try
        {
            if (!ActionsEnabled) throw new InvalidOperationException("actions disabled on the workstation");
            if (job.Error is not null) throw new ArgumentException(job.Error);
            var document = ResolveDocument(application, job.TargetDocument);
            var activeUiDocument = application.ActiveUIDocument;
            var uiDocument = activeUiDocument is not null
                             && activeUiDocument.Document.Title == document.Title
                             && activeUiDocument.Document.PathName == document.PathName
                ? activeUiDocument : null;
            var action = job.Action ?? throw new ArgumentException("Missing action arguments.");
            ActionResultData data;
            if (job.Command == "batch")
                data = BatchActionExecutor.Execute(document, uiDocument, action, failures);
            else
                data = ExecuteStep(document, uiDocument, job.Command, action, failures, out viewOpened);
            response = data.Committed == false && data.FailedStep.HasValue
                ? CommandResponse<ActionResultData>.Fail(job.Command, data.Steps!.Last().Error!, stopwatch.ElapsedMilliseconds)
                : CommandResponse<ActionResultData>.Ok(job.Command, data, stopwatch.ElapsedMilliseconds);
            response.Data = data;
            if (data.FailedStep.HasValue) response.Error = data.Steps!.Last().Error;
            if (!data.FailedStep.HasValue) response.WarningsDismissed = failures.WarningsDismissed;
        }
        catch (Exception exception)
        {
            response = CommandResponse<ActionResultData>.Fail(job.Command, exception.Message, stopwatch.ElapsedMilliseconds);
            response.Error = exception.Message;
            if (exception is ActionMutations.FamilyNotLoadedException missing)
                response.Data = new ActionResultData { ClosestFamilies = missing.ClosestFamilies };
            PluginLog.Error($"Action failed. Command='{job.Command}'.", exception);
        }
        finally
        {
            application.DialogBoxShowing -= SuppressDialog;
        }
        response.DialogsSuppressed = dialogsSuppressed;
        if (job.Command == "show") response.ViewOpened = viewOpened;
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
    }

    private static Document ResolveDocument(UIApplication application, string? reference)
    {
        if (reference is null)
            return application.ActiveUIDocument?.Document
                   ?? throw new InvalidOperationException("No active Revit document.");

        var candidates = application.Application.Documents.Cast<Document>()
            .Where(document => JobTargetMatcher.MatchesDocument(document.Title, document.PathName, reference))
            .ToList();
        return candidates.Count switch
        {
            0 => throw new InvalidOperationException($"The addressed document '{reference}' is not open."),
            1 => candidates[0],
            _ => throw new InvalidOperationException($"The document reference '{reference}' is ambiguous ({candidates.Count} open documents match); use a more specific substring.")
        };
    }

    internal static ActionResultData ExecuteStep(Document document, UIDocument? uiDocument, string command,
        ActionJobContract action, ActionFailures failures, out bool viewOpened, bool deferDryRun = false)
    {
        viewOpened = false;
        if (command is "select" or "show" or "isolate" && uiDocument is null)
            throw new InvalidOperationException($"Cannot run '{command}' on '{document.Title}' because it is not the active document; activate it in Revit first.");
        var ids = command == "isolate" && action.Reset ? [] : ResolveIds(document, action.ElementIds);
        if (command is "select" or "show")
        {
            if (command == "show")
            {
                viewOpened = OpenViewForElements(uiDocument!, ids);
                uiDocument!.ShowElements(ids);
            }
            if (command == "select" || action.Select) uiDocument!.Selection.SetElementIds(ids);
            return new ActionResultData { Count = uiDocument!.Selection.GetElementIds().Count };
        }

        using var transaction = new Transaction(document, "revit_" + command.Replace('-', '_'));
        if (transaction.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start the action transaction.");
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        try
        {
            var before = ActionVerifier.CaptureBefore(document, command, action, ids);
            var data = Mutate(document, command, action, ids);
            data.DryRun = action.DryRun;
            data.Verification ??= new ActionVerification();
            data.Verification.Before = before;
            if (command == "delete")
                before!.Dependents = data.Verification.Changed!.Except(before.Requested!).ToList();
            document.Regenerate();
            ActionVerifier.CaptureAfter(document, command, action, data);
            if (action.DryRun && !deferDryRun)
            {
                if (transaction.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("Could not roll back the dry run.");
                data.RolledBack = true;
            }
            else
            {
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException(failures.Message ?? "The action transaction was rolled back.");
                data.Verification.After = null;
                try
                {
                    ActionVerifier.CaptureAfter(document, command, action, data);
                }
                catch (Exception exception)
                {
                    data.Verification.Error = "Post-commit verification failed: " + exception.Message;
                    PluginLog.Error(data.Verification.Error, exception);
                }
            }
            return data;
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    public static void WriteError(UIApplication application, string command, string message, DateTimeOffset startedAt, string? correlationId = null)
    {
        var error = ActionsEnabled ? message : "actions disabled on the workstation";
        var response = CommandResponse<ActionResultData>.Fail(command, error, 0);
        response.Error = error;
        response.DialogsSuppressed = [];
        if (command == "show") response.ViewOpened = false;
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, command,
            ReadCommandReader.ReadResponder(application), correlationId).Write(response);
    }

    private static bool OpenViewForElements(UIDocument uiDocument, List<ElementId> ids)
    {
        var document = uiDocument.Document;
        using var idFilter = new ElementIdSetFilter(ids);
        foreach (var uiView in uiDocument.GetOpenUIViews())
        {
            if (!FilteredElementCollector.IsViewValidForElementIteration(document, uiView.ViewId)) continue;
            using var visible = document.CollectElements(uiView.ViewId).WherePasses(idFilter);
            if (visible.Any()) return false;
        }

        View? target = null;
        var levels = ids.Select(id => id.ToElement(document)?.LevelId.ToElement<Level>(document))
            .Where(level => level is not null).Distinct().ToList();
        if (levels.Count > 0)
        {
            using var planCollector = document.CollectElements().OfClass<ViewPlan>();
            var plans = planCollector.Cast<ViewPlan>()
                .Where(view => !view.IsTemplate).ToList();
            foreach (var level in levels)
            {
                target = plans.Where(view => view.GenLevel?.Id == level!.Id)
                    .OrderByDescending(view => view.ViewType == ViewType.FloorPlan)
                    .ThenByDescending(view => view.Name.StartsWith(level!.Name, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (target is not null) break;
            }
        }
        if (target is null)
        {
            using var viewCollector = document.CollectElements().OfClass<View3D>();
            target = viewCollector.Cast<View3D>().FirstOrDefault(view => !view.IsTemplate);
        }
        if (target is null) throw new InvalidOperationException("No non-template level plan or 3D view is available to show these elements.");

        var wasOpen = uiDocument.GetOpenUIViews().Any(view => view.ViewId == target.Id);
        // The ExternalEvent runs without a transaction; ShowElements requires the view change immediately.
        uiDocument.ActiveView = target;
        return !wasOpen;
    }

    private static ActionResultData Mutate(Document document, string command, ActionJobContract action, List<ElementId> ids)
    {
        switch (command)
        {
            case "isolate":
                if (action.Reset) document.ActiveView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                else document.ActiveView.IsolateElementsTemporary(ids);
                return new ActionResultData { Count = ids.Count };
            case "move":
                ElementTransformUtils.MoveElements(document, ids, new XYZ(Millimeters(action.DxMm), Millimeters(action.DyMm), Millimeters(action.DzMm)));
                return new ActionResultData { Count = ids.Count };
            case "delete":
                var deleted = document.Delete(ids).Select(RevitValueReader.GetId).OrderBy(value => value).ToList();
                return new ActionResultData
                {
                    Count = deleted.Count,
                    Verification = new ActionVerification { Changed = deleted }
                };
            case "place-family":
                return ActionMutations.PlaceFamily(document, action);
            case "create-wall":
                return ActionMutations.CreateWall(document, action);
            case "set-parameter":
                return ActionMutations.SetParameter(document, action);
            default:
                throw new ArgumentException($"Unknown action: {command}.");
        }
    }

    private static List<ElementId> ResolveIds(Document document, IEnumerable<long> values)
    {
        var ids = values.Select(CreateId).ToList();
        foreach (var id in ids)
            if (id.ToElement(document) is null) throw new ArgumentException($"Element {RevitValueReader.GetId(id)} was not found.");
        return ids;
    }

    internal static ElementId CreateId(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }

    private static double Millimeters(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

    internal sealed class ActionFailures : IFailuresPreprocessor
    {
        public List<string> WarningsDismissed { get; } = [];
        public string? Message { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var errors = new List<string>();
            var resolved = false;
            var rollBack = false;
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                var severity = failure.GetSeverity();
                if (severity == FailureSeverity.None) continue;
                var description = failure.GetDescriptionText();
                var resolution = FailureResolutionType.Invalid;
                // Other resolutions can delete, detach, skip or move elements outside the requested action.
                if (severity == FailureSeverity.Error && failure.HasResolutions())
                {
                    foreach (var candidate in new[] { FailureResolutionType.FixElements, FailureResolutionType.SetValue })
                    {
                        if (!failure.HasResolutionOfType(candidate)
                            || !failuresAccessor.IsFailureResolutionPermitted(failure, candidate)) continue;
                        resolution = candidate;
                        break;
                    }
                }
                var disposition = ActionFailurePolicy.Classify(
                    severity == FailureSeverity.Warning, severity == FailureSeverity.Error,
                    resolution != FailureResolutionType.Invalid,
                    severity == FailureSeverity.Error && failuresAccessor.GetAttemptedResolutionTypes(failure).Count > 0);
                if (disposition == ActionFailureDisposition.DismissWarning)
                {
                    failuresAccessor.DeleteWarning(failure);
                    WarningsDismissed.Add(description);
                    continue;
                }

                errors.Add(description);
                if (disposition == ActionFailureDisposition.ResolveError)
                {
                    try
                    {
                        failure.SetCurrentResolutionType(resolution);
                        failuresAccessor.ResolveFailure(failure);
                        resolved = true;
                        continue;
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException exception)
                    {
                        PluginLog.Error("Action failure resolution was rejected; rolling back.", exception);
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException exception)
                    {
                        PluginLog.Error("Action failure resolution was unavailable; rolling back.", exception);
                    }
                }
                rollBack = true;
            }
            Message = errors.Count > 0 ? string.Join("; ", errors) : null;
            if (rollBack) return FailureProcessingResult.ProceedWithRollBack;
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }
}
