using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Activity;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal static class ActionCommandExecutor
{
    internal static bool ReadOnlyMode => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "read-only"));

    public static void Execute(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        ActivityPaneAutoShow.EnsureShown();
        if (job.Command == "edit-families")
        {
            ExecuteFamilies(application, job, startedAt);
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        CommandResponse<ActionResultData> response;
        var dialogsSuppressed = new List<string>();
        var openWarningsDismissed = new List<string>();
        var viewOpened = false;
        var failures = new ActionFailures();
        Document? document = null;
        ActionResultData? data = null;
        void SuppressDialog(object? sender, DialogBoxShowingEventArgs arguments)
        {
            SuppressTaskDialog(arguments, dialogsSuppressed);
        }
        void SuppressOpenWarnings(object? sender, FailuresProcessingEventArgs arguments)
        {
            var accessor = arguments.GetFailuresAccessor();
            foreach (var warning in accessor.GetFailureMessages().Where(message => message.GetSeverity() == FailureSeverity.Warning))
            {
                openWarningsDismissed.Add(warning.GetDescriptionText());
                accessor.DeleteWarning(warning);
            }
            arguments.SetProcessingResult(FailureProcessingResult.Continue);
        }
        application.DialogBoxShowing += SuppressDialog;
        if (job.Command == "open-document") application.Application.FailuresProcessing += SuppressOpenWarnings;
        try
        {
            if (ReadOnlyMode) throw new InvalidOperationException("read-only mode");
            if (job.Error is not null) throw new ArgumentException(job.Error);
            if (job.Command is "open-document" or "close-document" or "save-document" or "sync-document")
            {
                var documentAction = job.Action ?? throw new ArgumentException("Missing document arguments.");
                documentAction.Document ??= job.TargetDocument;
                var documentResult = DocumentActions.Execute(application, job.Command, documentAction);
                documentResult.Summary = BuildDocumentSummary(job.Command, documentAction, documentResult);
                response = CommandResponse<ActionResultData>.Ok(job.Command, documentResult, stopwatch.ElapsedMilliseconds);
                response.DialogsSuppressed = dialogsSuppressed;
                response.WarningsDismissed = openWarningsDismissed;
                response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
                if (documentResult.NeedsConfirmation != true)
                    ActivityRecorder.RecordAction(job, null, documentResult, response);
                CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
                    ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
                return;
            }
            document = ResolveDocument(application, job.TargetDocument);
            var activeUiDocument = application.ActiveUIDocument;
            var uiDocument = activeUiDocument is not null
                             && activeUiDocument.Document.Title == document.Title
                             && activeUiDocument.Document.PathName == document.PathName
                ? activeUiDocument : null;
            var action = job.Action ?? throw new ArgumentException("Missing action arguments.");
            if (job.Command == "batch")
                data = BatchActionExecutor.Execute(document, uiDocument, action, failures, job.ClientName);
            else
                data = ExecuteStep(document, uiDocument, job.Command, action, failures, job.ClientName, out viewOpened);
            response = data.Committed == false && data.FailedStep.HasValue
                ? CommandResponse<ActionResultData>.Fail(job.Command, data.Steps!.Last().Error!, stopwatch.ElapsedMilliseconds)
                : CommandResponse<ActionResultData>.Ok(job.Command, data, stopwatch.ElapsedMilliseconds);
            response.Data = data;
            if (data.FailedStep.HasValue) response.Error = data.Steps!.Last().Error;
            if (!data.FailedStep.HasValue) response.WarningsDismissed = failures.WarningsDismissed;
        }
        catch (Exception exception)
        {
            var error = job.Command is "open-document" or "close-document" or "save-document" or "sync-document"
                ? exception.GetType().Name switch
                {
                    "CentralModelContentionException" => "The central model is locked or busy.",
                    "CentralModelAccessDeniedException" => "Access to the central model was denied.",
                    "RevitServerCommunicationException" => "Revit Server could not be reached.",
                    "WrongUserException" => "The local model belongs to another user.",
                    "CannotOpenBothCentralAndLocalException" => "The central and its local copy cannot be open together.",
                    _ => exception.Message
                }
                : exception.Message;
            response = CommandResponse<ActionResultData>.Fail(job.Command, error, stopwatch.ElapsedMilliseconds);
            response.Error = error;
            if (exception is ActionMutations.FamilyNotLoadedException missing)
                response.Data = new ActionResultData { ClosestFamilies = missing.ClosestFamilies };
            if (job.Command is "export-nwc" or "open-document" or "close-document" or "save-document" or "sync-document")
                PluginLog.Warn($"Action failed. Command='{job.Command}'; path and exception details omitted from log.");
            else PluginLog.Error($"Action failed. Command='{job.Command}'.", exception);
        }
        finally
        {
            application.DialogBoxShowing -= SuppressDialog;
            if (job.Command == "open-document") application.Application.FailuresProcessing -= SuppressOpenWarnings;
        }
        response.DialogsSuppressed = dialogsSuppressed;
        if (job.Command == "show") response.ViewOpened = viewOpened;
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        ActivityRecorder.RecordAction(job, document, data, response);
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
    }

    private static void ExecuteFamilies(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        var stopwatch = Stopwatch.StartNew();
        var dialogsSuppressed = new List<string>();
        Document? document = null;
        FamilyEditData? result = null;
        void SuppressDialog(object? sender, DialogBoxShowingEventArgs arguments)
        {
            SuppressTaskDialog(arguments, dialogsSuppressed);
        }
        application.DialogBoxShowing += SuppressDialog;
        CommandResponse<FamilyEditData>? response = null;
        try
        {
            if (ReadOnlyMode)
                throw new InvalidOperationException("read-only mode");
            if (job.Error is not null) throw new ArgumentException(job.Error);
            document = ResolveDocument(application, job.TargetDocument);
            var action = job.Action ?? throw new ArgumentException("Missing family arguments.");
            ActionJobParser.ValidateFamilyMode(action, document.IsFamilyDocument);
            var output = CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
                ReadCommandReader.ReadResponder(application), job.CorrelationId);
            var failures = new ActionFailures();
            result = FamilyEditor.Execute(document, action, failures, application.Application, job.ClientName);
            var failed = !result.Committed && result.FailedFamily is not null;
            var error = failed ? result.Families.First(family => family.Status == "failed").Reason ?? "Family edit failed." : null;
            response = failed
                ? CommandResponse<FamilyEditData>.Fail(job.Command, error!, stopwatch.ElapsedMilliseconds)
                : CommandResponse<FamilyEditData>.Ok(job.Command, result, stopwatch.ElapsedMilliseconds);
            response.Data = result;
            response.Error = error;
            response.DialogsSuppressed = dialogsSuppressed;
            response.WarningsDismissed = failures.WarningsDismissed;
            output.Write(response);
        }
        catch (Exception exception)
        {
            response = CommandResponse<FamilyEditData>.Fail(job.Command, exception.Message, stopwatch.ElapsedMilliseconds);
            response.Error = exception.Message;
            response.DialogsSuppressed = dialogsSuppressed;
            CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
                ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
            PluginLog.Error($"Family command failed. Command='{job.Command}'.", exception);
        }
        finally
        {
            application.DialogBoxShowing -= SuppressDialog;
        }
        ActivityRecorder.RecordFamilyEdit(job, document, result, response!);
    }

    private static void SuppressTaskDialog(DialogBoxShowingEventArgs arguments, List<string> dialogsSuppressed)
    {
        if (arguments is not TaskDialogShowingEventArgs dialog) return;
        if (dialog.OverrideResult((int)TaskDialogResult.Ok) || dialog.OverrideResult((int)TaskDialogResult.Yes))
            dialogsSuppressed.Add(dialog.Message);
    }

    internal static Document ResolveDocument(UIApplication application, string? reference)
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
        ActionJobContract action, ActionFailures failures, string clientName, out bool viewOpened,
        bool deferDryRun = false, bool wrapGroup = true)
    {
        viewOpened = false;
        if (command == "undo-last") return ExecuteUndoLast(document, uiDocument);
        if (command == "export-nwc")
        {
            var exportResult = NwcExporter.Execute(document, action);
            exportResult.Summary = BuildSummary(command, action, exportResult, document.Title, null);
            return exportResult;
        }
        if (command == "align-link-datums")
            return AlignLinkDatums.Execute(document, action.DatumOptions!, action.DryRun, failures, clientName);
        if (command == "set-view-visibility")
            return ViewVisibility.Execute(document, action.Visibility!, action.DryRun, failures, clientName);
        if (command == "remove-links")
            return LinkRemoval.Execute(document, action.LinkRemoval!, action.DryRun, failures, clientName);
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
            var viewData = new ActionResultData { Count = uiDocument!.Selection.GetElementIds().Count };
            viewData.Summary = BuildSummary(command, action, viewData, document.Title, ids);
            return viewData;
        }

        var group = wrapGroup ? new TransactionGroup(document, "MCP action") : null;
        if (group is not null && group.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start the action transaction group.");
        try
        {
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
                data.Summary = BuildSummary(command, action, data, document.Title, ids);
                if (action.DryRun && !deferDryRun)
                {
                    if (transaction.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Could not roll back the dry run.");
                    data.RolledBack = true;
                    group?.RollBack();
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
                    if (group is not null)
                    {
                        var groupName = ActionSummaryBuilder.BuildGroupName(clientName, data.Summary);
                        group.SetName(groupName);
                        if (group.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Could not assimilate the action transaction group.");
                        data.UndoName = groupName;
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
        catch
        {
            if (group is not null && group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
        finally
        {
            group?.Dispose();
        }
    }

    internal static ActionResultData ExecuteUndoLast(Document document, UIDocument? uiDocument)
    {
        var newest = ActivityLog.Newest();
        var (trackedDocumentTitle, lastTransactionName) = UndoTracker.Snapshot();
        var isActiveDocument = uiDocument is not null && string.Equals(trackedDocumentTitle, document.Title, StringComparison.Ordinal);
        var undoCommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
        // document.IsModifiable only reflects an open transaction, which is never the case at this call
        // site; CanPostCommand is what actually reflects a pending interactive Revit command.
        var hasPendingCommand = uiDocument is not null && !uiDocument.Application.CanPostCommand(undoCommandId);
        if (!UndoEligibility.IsAllowed(isActiveDocument, hasPendingCommand, newest?.UndoEntryName, lastTransactionName, out var reason))
            throw new InvalidOperationException(reason);
        uiDocument!.Application.PostCommand(undoCommandId);
        return new ActionResultData
        {
            Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
            {
                Command = "undo-last", DocumentTitle = document.Title
            })
        };
    }

    private static string BuildSummary(string command, ActionJobContract action, ActionResultData data,
        string documentTitle, List<ElementId>? ids)
    {
        var count = command switch
        {
            "move" or "select" or "isolate" => ids?.Count ?? 0,
            "show" => data.Count ?? ids?.Count ?? 0,
            "delete" => data.Verification?.Changed?.Count ?? ids?.Count ?? 0,
            _ => 0
        };
        return ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = command,
            DocumentTitle = documentTitle,
            Count = count,
            DryRun = action.DryRun,
            Family = action.Family,
            TypeName = action.TypeName,
            Parameter = action.Parameter,
            WallType = action.WallType,
            BatchStepCount = action.Steps.Count
        });
    }

    private static string BuildDocumentSummary(string command, ActionJobContract action, ActionResultData data)
    {
        var title = data.Title ?? action.Document ?? "the document";
        return ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = command,
            DocumentTitle = title,
            OpenedAs = data.OpenedAs,
            Saved = data.Saved == true,
            TargetPath = command == "save-document" ? action.SaveAs : null,
            NeedsConfirmation = data.NeedsConfirmation == true,
            ConfirmationText = data.ConfirmationText
        });
    }

    public static void WriteError(UIApplication application, string command, string message, DateTimeOffset startedAt, string? correlationId = null)
    {
        var error = ReadOnlyMode ? "read-only mode" : message;
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

    internal static List<ElementId> ResolveIds(Document document, IEnumerable<long> values)
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

    internal static double Millimeters(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

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
