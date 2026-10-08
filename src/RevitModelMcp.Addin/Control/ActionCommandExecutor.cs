using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Activity;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Formatting;
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
        if (job.Command == "process-models")
        {
            ExecuteProcessModels(application, job, startedAt);
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        CommandResponse<ActionResultData> response;
        var dialogsSuppressed = new List<string>();
        var openWarningsDismissed = new List<string>();
        var openWarningCount = 0;
        var openAttempted = false;
        var viewOpened = false;
        var failures = new ActionFailures();
        Document? document = null;
        ActionResultData? data = null;
        ChangeCapture? changes = null;
        void SuppressDialog(object? sender, DialogBoxShowingEventArgs arguments)
        {
            SuppressTaskDialog(arguments, dialogsSuppressed);
        }
        void SuppressOpenWarnings(object? sender, FailuresProcessingEventArgs arguments)
        {
            openWarningCount += DismissOpenWarnings(arguments, openWarningsDismissed);
        }
        application.DialogBoxShowing += SuppressDialog;
        if (job.Command == "open-document") application.Application.FailuresProcessing += SuppressOpenWarnings;
        try
        {
            if (ReadOnlyMode || Environment.GetEnvironmentVariable("REVIT_MCP_BATCH_WORKER") == "1") throw new InvalidOperationException("read-only mode");
            if (job.Error is not null) throw new ArgumentException(job.Error);
            if (job.Command is "open-document" or "close-document" or "save-document" or "sync-document" or "activate-document" or "activate-view" or "close-views" or "new-document")
            {
                var documentAction = job.Action ?? throw new ArgumentException("Missing document arguments.");
                documentAction.Document ??= job.TargetDocument;
                if (job.Command == "open-document") openAttempted = true;
                var documentResult = DocumentActions.Execute(application, job.Command, documentAction);
                documentResult.Summary = BuildDocumentSummary(job.Command, documentAction, documentResult);
                response = CommandResponse<ActionResultData>.Ok(job.Command, documentResult, stopwatch.ElapsedMilliseconds);
                response.DialogsSuppressed = dialogsSuppressed;
                response.WarningsDismissed = openWarningsDismissed;
                response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
                if (documentResult.NeedsConfirmation != true)
                    ActivityRecorder.RecordAction(job, null, documentResult, response, null);
                CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
                    ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
                return;
            }
            document = job.Command == "execute-code" && job.TargetDocument is null
                ? application.ActiveUIDocument?.Document
                : ResolveDocument(application, job.TargetDocument);
            if (document is not null && !(job.Command == "execute-code" && job.Action?.TransactionMode == "none"))
                changes = ChangeCapture.Start(application.Application, document);
            var activeUiDocument = application.ActiveUIDocument;
            var uiDocument = document is not null && activeUiDocument is not null
                             && activeUiDocument.Document.Title == document.Title
                             && activeUiDocument.Document.PathName == document.PathName
                ? activeUiDocument : null;
            var action = job.Action ?? throw new ArgumentException("Missing action arguments.");
            if (job.Command is "export" or "export-nwc") action.ConfirmOverwrites = true;
            if (job.Command == "execute-code")
            {
                if (!action.DryRun)
                    data = ConfirmationStore.Gate("execute-code",
                        document is null ? "none" : DocumentConfirmationBinding.Identity(document.PathName, document.Title),
                        DocumentConfirmationBinding.CodeArguments(action.Code!, action.TransactionMode), "", action.ConfirmToken,
                        $"Run C# code (SHA-256 {DocumentConfirmationBinding.CodeHash(action.Code!)[..12]}, {action.Code!.Length} characters, transaction {action.TransactionMode}) on '{document?.Title ?? "no document"}'. The code has full Revit API access and the user's file and network rights. Review it before confirming.",
                        "Needs confirmation to run C# code.",
                        "The confirmation state changed. Repeat the call without confirm_token to get a new one.");
                data ??= CodeExecution.Execute(application, document, uiDocument, action, failures, job.ClientName, job.JobId);
            }
            else if (job.Command == "batch")
                data = BatchActionExecutor.Execute(document!, uiDocument, action, failures, job.ClientName);
            else
                data = ExecuteStep(document!, uiDocument, job.Command, action, failures, job.ClientName, out viewOpened);
            response = data.CodeError is not null
                ? CommandResponse<ActionResultData>.Fail(job.Command, data.CodeError, stopwatch.ElapsedMilliseconds)
                : data.Committed == false && data.FailedStep.HasValue
                ? CommandResponse<ActionResultData>.Fail(job.Command, data.Steps!.Last().Error!, stopwatch.ElapsedMilliseconds)
                : CommandResponse<ActionResultData>.Ok(job.Command, data, stopwatch.ElapsedMilliseconds);
            response.Data = data;
            if (data.CodeError is not null) response.Error = data.CodeError;
            if (data.FailedStep.HasValue) response.Error = data.Steps!.Last().Error;
            if (!data.FailedStep.HasValue) response.WarningsDismissed = failures.WarningsDismissed;
        }
        catch (Exception exception)
        {
            var error = job.Command is "open-document" or "close-document" or "save-document" or "sync-document" or "activate-document" or "activate-view" or "close-views" or "new-document"
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
            if (exception is ActionMutations.MatchLimitException limit)
                response.Data = new ActionResultData { MatchedCount = limit.Count, Count = limit.Count };
            if (job.Command is "export-nwc" or "load-family" or "place-families" or "export" or "link-cad" or "open-document" or "close-document" or "save-document" or "sync-document" or "activate-document" or "activate-view" or "close-views" or "new-document")
                PluginLog.Warn($"Action failed. Command='{job.Command}'; path and exception details omitted from log.");
            else if (job.Command == "execute-code")
                PluginLog.Warn("Code execution failed. Source and exception details omitted from log.");
            else PluginLog.Error($"Action failed. Command='{job.Command}'.", exception);
        }
        finally
        {
            application.DialogBoxShowing -= SuppressDialog;
            changes?.Dispose();
            if (job.Command == "open-document") application.Application.FailuresProcessing -= SuppressOpenWarnings;
            if (openAttempted) PluginLog.Info($"Dismissed {openWarningCount} warnings during model open.");
        }
        response.DialogsSuppressed = dialogsSuppressed;
        if (job.Command == "show") response.ViewOpened = viewOpened;
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        if (data?.NeedsConfirmation != true)
            ActivityRecorder.RecordAction(job,
                job.Command == "execute-code" && job.Action?.TransactionMode == "none" ? null : document,
                data, response, changes);
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
    }

    internal static int DismissOpenWarnings(FailuresProcessingEventArgs arguments, ICollection<string>? descriptions = null)
    {
        var accessor = arguments.GetFailuresAccessor();
        var count = 0;
        foreach (var warning in accessor.GetFailureMessages().Where(message => message.GetSeverity() == FailureSeverity.Warning))
        {
            descriptions?.Add(warning.GetDescriptionText());
            accessor.DeleteWarning(warning);
            count++;
        }
        arguments.SetProcessingResult(FailureProcessingResult.Continue);
        return count;
    }

    private static void ExecuteProcessModels(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        var stopwatch = Stopwatch.StartNew();
        ActionResultData? data = null;
        CommandResponse<ActionResultData> response;
        try
        {
            if (ReadOnlyMode || Environment.GetEnvironmentVariable("REVIT_MCP_BATCH_WORKER") == "1")
                throw new InvalidOperationException("read-only mode");
            if (job.Error is not null) throw new ArgumentException(job.Error);
            var request = job.Action?.ProcessModels ?? throw new ArgumentException("process is required.");
            var paths = request.Paths ?? Directory.EnumerateFiles(request.Folder!, request.Pattern ?? "*.rvt",
                request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count is < 1 or > 500) throw new ArgumentException("The source must resolve to 1 to 500 models.");
            foreach (var path in paths) DocumentPathValidator.Validate(path);
            if (paths.Select(path => path.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
                throw new ArgumentException("The source contains duplicate models.");
            if (request.Save?.Mode == "output_dir" ||
                request.Exports?.Any(export => export.Folder is null || export.Folder.Contains("{model}")) == true)
            {
                var collisions = ProcessOutputCollisions.Find(paths);
                if (collisions.Count > 0) throw new ArgumentException(ProcessOutputCollisions.FormatError(collisions));
            }
            var refused = new List<ProcessModelResult>();
            if (request.Save?.Mode == "in_place" && !request.DryRun)
            {
                var (writable, readOnly) = ProcessModelsJob.SelectWritableInPlacePaths(paths,
                    path => new FileInfo(path).IsReadOnly);
                refused = readOnly.Select(path => new ProcessModelResult
                { Path = path, Status = "refused", Error = "read-only file" }).ToList();
                paths = writable;
                if (paths.Count == 0)
                {
                    data = new ActionResultData { Models = refused, Total = refused.Count };
                    response = CommandResponse<ActionResultData>.Fail(job.Command,
                        "All source models were refused: read-only file.", stopwatch.ElapsedMilliseconds);
                    response.Data = data;
                    WriteProcessResponse(application, job, startedAt, response);
                    return;
                }
            }
            if (!request.DryRun && (request.Save?.Mode == "in_place" || request.Code is not null))
            {
                var identity = string.Join("\n", paths);
                var arguments = ProcessConfirmationArguments(request);
                var state = string.Join("\n", paths.Select(path => File.Exists(path)
                    ? $"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}" : "missing"));
                var confirmationText = request.Save?.Mode == "in_place"
                    ? "Save in place will overwrite these source models: " + string.Join(", ", paths)
                    : string.Empty;
                if (request.Code is not null)
                {
                    var code = request.Code.Code!;
                    if (confirmationText.Length > 0) confirmationText += "\n";
                    confirmationText += $"Run C# code (SHA-256 {DocumentConfirmationBinding.CodeHash(code)[..12]}, {code.Length} characters, transaction {request.Code.Transaction ?? "auto"}) in each of these models: " + string.Join(", ", paths);
                }
                if (request.ConfirmToken is null)
                {
                    data = new ActionResultData
                    {
                        NeedsConfirmation = true,
                        Models = refused,
                        Total = paths.Count + refused.Count,
                        ConfirmationText = confirmationText,
                        ConfirmToken = ConfirmationStore.Tokens.Issue("process-models", identity, arguments, state),
                        Summary = request.Save?.Mode == "in_place"
                            ? $"Needs confirmation to save {paths.Count} models in place{(request.Code is null ? "" : " and run code")}."
                            : $"Needs confirmation to run code on {paths.Count} models."
                    };
                    response = CommandResponse<ActionResultData>.Ok(job.Command, data, stopwatch.ElapsedMilliseconds);
                    WriteProcessResponse(application, job, startedAt, response);
                    return;
                }
                var confirmation = ConfirmationStore.Tokens.Consume(request.ConfirmToken, "process-models", identity, arguments, state);
                if (confirmation != DocumentConfirmationResult.Valid)
                    throw new InvalidOperationException(ConfirmationStore.Rejection(confirmation,
                        "The source models changed on disk after the preview (size or modification time). The confirmation token is used up; run the call again without confirm_token to get a new one."));
            }
            else if (request.ConfirmToken is not null && request.Save?.Mode != "in_place" && request.Code is null)
                throw new ArgumentException("confirm_token applies only to in_place saves or C# code.");
            data = new ActionResultData { Models = refused, DryRun = request.DryRun, Total = paths.Count + refused.Count };
            for (var index = 0; index < paths.Count; index++)
            {
                if (ResponseDelivery.CancellationRequested?.Invoke() == true)
                {
                    ProcessModelsResultAssembler.AppendCancelled(data.Models, paths, index);
                    data.Cancelled = true;
                    break;
                }
                var path = paths[index];
                data.CurrentIndex = data.Models.Count + 1;
                data.CurrentPath = path;
                WriteProcessResponse(application, job, startedAt,
                    CommandResponse<ActionResultData>.PartialResult(job.Command, data,
                        "Command accepted and running.", stopwatch.ElapsedMilliseconds));
                var model = ProcessOneModel(application, job, request, path, paths);
                data.Models.Add(model);
                WriteProcessResponse(application, job, startedAt,
                    CommandResponse<ActionResultData>.PartialResult(job.Command, data,
                        "Command accepted and running.", stopwatch.ElapsedMilliseconds));
                if (model.Status == "failed" && request.StopOnError) break;
            }
            data.Done = data.Models.Count(model => model.Status == "done");
            data.Failed = data.Models.Count(model => model.Status == "failed");
            data.SkippedCount = data.Models.Count(model => model.Status == "skipped");
            data.Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
            {
                Command = "process-models",
                Count = data.Done.Value,
                ProcessTotal = paths.Count,
                ProcessFailed = (int)data.Failed!,
                ProcessSkipped = data.SkippedCount.Value,
                ProcessCancelled = data.Models.Count(model => model.Status == "cancelled"),
                DryRun = request.DryRun
            });
            if (data.Cancelled == true) data.Summary += " Cancelled before the next model; completed changes remain committed.";
            response = (int)data.Failed! > 0 || data.Cancelled == true
                ? CommandResponse<ActionResultData>.PartialResult(job.Command, data, data.Summary, stopwatch.ElapsedMilliseconds)
                : CommandResponse<ActionResultData>.Ok(job.Command, data, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            response = CommandResponse<ActionResultData>.Fail(job.Command, exception.Message, stopwatch.ElapsedMilliseconds);
            response.Error = exception.Message;
            PluginLog.Warn("Model processing failed; paths and exception details omitted from log.");
        }
        if (data?.NeedsConfirmation != true)
            ActivityRecorder.RecordAction(job, null, data, response, null);
        WriteProcessResponse(application, job, startedAt, response);
    }

    private static string ProcessConfirmationArguments(ProcessModelsJob request)
    {
        var token = request.ConfirmToken;
        try
        {
            request.ConfirmToken = null;
            using var stream = new MemoryStream();
            new DataContractJsonSerializer(typeof(ProcessModelsJob)).WriteObject(stream, request);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        finally { request.ConfirmToken = token; }
    }

    private static void WriteProcessResponse(UIApplication application, ControlJobParseResult job,
        DateTimeOffset startedAt, CommandResponse<ActionResultData> response)
    {
        response.ActiveView = application.ActiveUIDocument?.ActiveView?.Name ?? string.Empty;
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
    }

    private static ProcessModelResult ProcessOneModel(UIApplication application, ControlJobParseResult job,
        ProcessModelsJob request, string path, IReadOnlyCollection<string> sources)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new ProcessModelResult { Path = path };
        var dismissedMessages = new List<string>();
        Document? document = null;
        ChangeCapture? changes = null;
        TransactionGroup? group = null;
        string? undoName = null;
        var failures = new ActionFailures();
        void SuppressDialog(object? sender, DialogBoxShowingEventArgs arguments) =>
            SuppressTaskDialog(arguments, dismissedMessages);
        void SuppressWarnings(object? sender, FailuresProcessingEventArgs arguments) =>
            DismissOpenWarnings(arguments, dismissedMessages);
        application.DialogBoxShowing += SuppressDialog;
        application.Application.FailuresProcessing += SuppressWarnings;
        try
        {
            if (application.Application.Documents.Cast<Document>().Any(item =>
                DocumentPathValidator.SamePath(item.PathName, path)))
            {
                result.Status = "skipped";
                result.Error = "The model is already open in this Revit session.";
                return result;
            }
            var opening = request.Open ?? new ControlJobContract();
            var (openedDocument, openedResult) = DocumentActions.OpenForProcessing(application, new ActionJobContract
            {
                DocumentPath = path,
                Mode = opening.Mode ?? "detached",
                Worksets = opening.Worksets ?? "all",
                WorksetsOpenNames = opening.WorksetsOpen,
                WorksetsCloseNames = opening.WorksetsClose,
                Audit = opening.Audit ?? false
            });
            document = openedDocument;
            result.Opened = new ProcessModelOpenedResult
            {
                Mode = openedResult.OpenedAs ?? opening.Mode ?? "detached",
                Worksets = openedResult.WorksetsOpen ?? [],
                Audited = openedResult.Audited ?? false,
                WorksetPatternsUnmatched = openedResult.WorksetPatternsUnmatched ?? [],
                Warning = openedResult.Warning
            };
            var save = request.Save ?? new ProcessSaveJob();
            save.EnsureInPlaceAllowed(document.IsWorkshared);
            var sourceFileName = Path.GetFileName(path.Replace('/', '\\'));
            var saveTarget = save.Mode == "output_dir" ? Path.Combine(save.OutputDir!, sourceFileName) : path;
            if (save.Mode == "output_dir" && sources.Any(source => DocumentPathValidator.SamePath(source, saveTarget)))
                throw new InvalidOperationException("The save target matches a source model.");
            changes = ChangeCapture.Start(application.Application, document);
            if (request.Steps is not null || request.Code is not null)
            {
                group = new TransactionGroup(document, "MCP process model");
                if (group.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the model transaction group.");
            }
            if (request.Steps is not null)
            {
                var batch = ActionJobParser.Parse("batch", new ControlJobContract { Steps = request.Steps }).Action!;
                result.Steps = BatchActionExecutor.Execute(document, null, batch, failures, job.ClientName);
                if (result.Steps.FailedStep.HasValue)
                    throw new InvalidOperationException(result.Steps.Steps?.LastOrDefault()?.Error ?? "Model steps failed.");
            }
            if (request.Code is not null)
            {
                var codeAction = new ActionJobContract
                {
                    Code = request.Code.Code,
                    TransactionMode = request.Code.Transaction ?? "auto"
                };
                var code = CodeExecution.Execute(application, document, null, codeAction, failures, job.ClientName, job.JobId);
                result.Code = new ProcessModelCodeResult
                {
                    ReturnValue = code.ReturnValue,
                    ReturnValueMarker = Guid.NewGuid().ToString("N"),
                    Log = code.Log ?? []
                };
                if (code.CodeError is not null) throw new InvalidOperationException(code.CodeError);
            }
            var modelName = Path.GetFileNameWithoutExtension(path.Replace('/', '\\'));
            if (request.Exports is not null)
            {
                result.Exports = [];
                foreach (var export in request.Exports)
                {
                    var baseFolder = request.Save?.OutputDir ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "exports");
                    var folder = export.Folder is null
                        ? Path.Combine(baseFolder, modelName)
                        : export.Folder.Replace("{model}", modelName);
                    var parsed = ActionJobParser.Parse("export", new ControlJobContract
                    {
                        Format = export.Format,
                        Views = export.Views,
                        Sheets = export.Sheets,
                        SheetSet = export.SheetSet,
                        AllSheets = export.AllSheets,
                        Folder = folder,
                        ExportOptions = export.ExportOptions,
                        Overwrite = export.Overwrite
                    });
                    if (parsed.Error is not null) throw new ArgumentException(parsed.Error);
                    var exportAction = parsed.Action!;
                    exportAction.DryRun = request.DryRun;
                    result.Exports.Add(ExecuteStep(document, null, "export", exportAction, failures,
                        job.ClientName, out _));
                }
            }
            if (group is not null)
            {
                if (request.DryRun)
                {
                    if (group.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Could not roll back the model preview.");
                    MarkProcessBatchRolledBack(result.Steps, document.Title, true);
                }
                else
                {
                    undoName = ActionSummaryBuilder.BuildGroupName(job.ClientName, $"Process {modelName}");
                    group.SetName(undoName);
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Could not commit the model changes.");
                }
            }
            if (!request.DryRun && save.Mode is ("output_dir" or "in_place"))
            {
                if (save.Mode == "output_dir")
                    Directory.CreateDirectory(save.OutputDir!);
                DocumentActions.SaveForProcessing(application, document,
                    save.Mode == "output_dir" ? saveTarget : null, save.Compact ?? true, save.Overwrite);
                result.Saved = saveTarget;
            }
            result.Status = "done";
        }
        catch (Exception exception)
        {
            result.Error = exception.Message;
            if (group?.GetStatus() == TransactionStatus.Started)
            {
                try
                {
                    group.RollBack();
                    MarkProcessBatchRolledBack(result.Steps, document?.Title ?? string.Empty, request.DryRun);
                }
                catch (Exception rollbackException)
                {
                    result.Error += " Rollback failed: " + rollbackException.Message;
                }
            }
            result.Status = "failed";
        }
        finally
        {
            void Cleanup(string step, bool critical, Action action)
            {
                try { action(); }
                catch (Exception exception)
                {
                    var sentence = $"Cleanup failed ({step}): {exception.Message}";
                    if (critical)
                    {
                        result.Status = "failed";
                        result.Error = result.Error is null ? sentence : result.Error + " " + sentence;
                    }
                    else
                        (result.Warnings ??= []).Add(sentence);
                }
            }
            Cleanup("transaction group", false, () => group?.Dispose());
            dismissedMessages.AddRange(failures.WarningsDismissed);
            if (document is not null)
            {
                Cleanup("activity record", false, () =>
                {
                    var activity = new ActionResultData
                    {
                        Title = document.Title,
                        DryRun = request.DryRun,
                        UndoName = undoName,
                        Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
                        {
                            Command = "process-models",
                            DocumentTitle = document.Title,
                            DryRun = request.DryRun
                        })
                    };
                    var activityResponse = result.Status == "done"
                        ? CommandResponse<ActionResultData>.Ok(job.Command, activity, stopwatch.ElapsedMilliseconds)
                        : CommandResponse<ActionResultData>.Fail(job.Command, result.Error ?? "Model processing failed.", stopwatch.ElapsedMilliseconds);
                    ActivityRecorder.RecordAction(job, document, activity, activityResponse, changes);
                });
            }
            Cleanup("change capture", false, () => changes?.Dispose());
            if (document is not null)
                Cleanup("close", true, () => DocumentActions.CloseForProcessing(application, document));
            Cleanup("dialog handler", true, () => application.DialogBoxShowing -= SuppressDialog);
            Cleanup("warning handler", true, () => application.Application.FailuresProcessing -= SuppressWarnings);
            result.DialogsDismissed = ProcessDialogSummary.FromMessages(dismissedMessages);
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
        return result;
    }

    private static void MarkProcessBatchRolledBack(ActionResultData? steps, string documentTitle, bool preview)
    {
        if (steps is null) return;
        steps.Committed = false;
        steps.RolledBack = true;
        steps.UndoName = null;
        steps.DryRun = preview;
        if (preview) steps.Summary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = "batch",
            DocumentTitle = documentTitle,
            DryRun = true,
            BatchStepCount = steps.Steps?.Count ?? 0
        });
        foreach (var step in steps.Steps ?? [])
        {
            step.RolledBack = true;
            if (step.Data is not null) step.Data.RolledBack = true;
        }
    }

    private static void ExecuteFamilies(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        var stopwatch = Stopwatch.StartNew();
        var dialogsSuppressed = new List<string>();
        Document? document = null;
        FamilyEditData? result = null;
        ChangeCapture? changes = null;
        void SuppressDialog(object? sender, DialogBoxShowingEventArgs arguments)
        {
            SuppressTaskDialog(arguments, dialogsSuppressed);
        }
        application.DialogBoxShowing += SuppressDialog;
        CommandResponse<FamilyEditData>? response = null;
        try
        {
            if (ReadOnlyMode || Environment.GetEnvironmentVariable("REVIT_MCP_BATCH_WORKER") == "1")
                throw new InvalidOperationException("read-only mode");
            if (job.Error is not null) throw new ArgumentException(job.Error);
            document = ResolveDocument(application, job.TargetDocument);
            changes = ChangeCapture.Start(application.Application, document);
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
            changes?.Dispose();
        }
        ActivityRecorder.RecordFamilyEdit(job, document, result, response!, changes);
    }

    private static void SuppressTaskDialog(DialogBoxShowingEventArgs arguments, List<string> dialogsSuppressed)
    {
        if (arguments is not TaskDialogShowingEventArgs dialog) return;
        if (dialog.OverrideResult((int)TaskDialogResult.Ok) || dialog.OverrideResult((int)TaskDialogResult.Yes))
            dialogsSuppressed.Add(dialog.Message);
    }

    private static ActionResultData ExportFiles(Document document, ActionJobContract action)
    {
        if (document.IsFamilyDocument) throw new ArgumentException("File export requires a project document.");
        var request = action.Export;
        request.Validate();
        var folder = request.Folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp", "exports",
            FileExportJob.FileName(document.Title, "tmp")[..^4], DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"));
        var options = request.Options;
        var selected = new List<View>();
        void Add(View view)
        {
            if (view.IsTemplate || request.Format is "pdf" or "dwg" && !view.CanBePrinted)
                throw new ArgumentException($"View '{view.Name}' cannot be printed.");
            if (selected.All(existing => existing.Id != view.Id)) selected.Add(view);
        }
        foreach (var reference in request.Views ?? [])
            Add(ReadCommandReader.FindView(document, reference) ?? throw new ArgumentException($"View '{reference}' was not found."));
        using var sheetCollector = new FilteredElementCollector(document).OfClass(typeof(ViewSheet));
        var allSheets = sheetCollector.Cast<ViewSheet>().Where(sheet => !sheet.IsTemplate).ToList();
        foreach (var reference in request.Sheets ?? [])
        {
            var sheet = ViewReferenceMatcher.Find(allSheets, reference,
                item => RevitValueReader.GetId(item.Id), item => item.Name,
                item => item.SheetNumber, item => item.ViewType.ToString(), ReadCommandReader.SheetCollectionName);
            Add(sheet ?? throw new ArgumentException($"Sheet '{reference}' was not found."));
        }
        if (request.SheetSet is not null)
        {
            using var sets = new FilteredElementCollector(document).OfClass(typeof(ViewSheetSet));
            var set = sets.Cast<ViewSheetSet>().FirstOrDefault(item => item.Name == request.SheetSet)
                ?? throw new ArgumentException($"Sheet set '{request.SheetSet}' was not found.");
            foreach (View view in set.Views) Add(view);
        }
        if (request.AllSheets) foreach (var sheet in allSheets) Add(sheet);
        if (request.Format == "csv")
        {
            if (request.Views is null)
            {
                using var schedules = new FilteredElementCollector(document).OfClass(typeof(ViewSchedule));
                foreach (var schedule in schedules.Cast<ViewSchedule>().Where(item => !item.IsTemplate &&
                    !item.IsTitleblockRevisionSchedule && !item.IsInternalKeynoteSchedule)) Add(schedule);
            }
            if (selected.Any(view => view is not ViewSchedule))
                throw new ArgumentException("CSV targets must be schedules.");
        }
        if (request.Format == "ifc" && selected.Count > 1)
            throw new ArgumentException("IFC accepts at most one view.");
        if (request.Format is "pdf" or "dwg" && selected.Count == 0)
            throw new ArgumentException("No printable targets were resolved.");
        var extension = request.Format;
        var planned = request.Format switch
        {
            "ifc" => new List<string> { FileExportJob.FileName(options.FileName ?? document.Title, extension) },
            "pdf" when options.Combine ?? true => [FileExportJob.FileName(options.FileName ?? document.Title, extension)],
            _ => selected.Select(view => FileExportJob.FileName(
                view is ViewSheet sheet && options.Naming != "view_name" ? $"{sheet.SheetNumber}_{sheet.Name}" : view.Name,
                extension)).ToList()
        };
        if (planned.Count != planned.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new ArgumentException("Export targets produce duplicate file names.");
        if (Directory.Exists(folder) && !request.Overwrite && planned.Any(name => File.Exists(Path.Combine(folder, name))))
            throw new ArgumentException("An export file already exists; set overwrite=true.");
        if (request.Overwrite && !action.DryRun && action.ConfirmOverwrites)
        {
            var existing = planned.Select(name => Path.Combine(folder, name)).Where(File.Exists).ToList();
            if (existing.Count > 0)
            {
                var gate = ConfirmationStore.Gate("export",
                    DocumentConfirmationBinding.Identity(document.PathName, document.Title),
                    DocumentConfirmationBinding.ExportArguments("export", existing),
                    DocumentConfirmationBinding.FileState(existing), action.ConfirmToken,
                    "Exporting will overwrite these existing files: " + string.Join(", ", existing),
                    $"Needs confirmation to overwrite {existing.Count} export files.",
                    "The export targets changed on disk after the preview (size or modification time). The confirmation token is used up; repeat the call without confirm_token to get a new one.");
                if (gate is not null) return gate;
            }
        }
        var result = new ActionResultData
        {
            Folder = folder,
            Files = planned.Select(name => new ExportedFile { Name = name }).ToList(),
            Targets = selected.Select(view => view is ViewSheet sheet ? $"{sheet.SheetNumber}: {sheet.Name}" : view.Name).ToList(),
            Skipped = new List<string>(),
            DryRun = action.DryRun,
            ElapsedMs = 0,
            Summary = $"{(action.DryRun ? "Would export" : "Exported")} {planned.Count} {request.Format.ToUpperInvariant()} file(s) from {document.Title}."
        };
        if (action.DryRun) return result;
        Directory.CreateDirectory(folder);
        var stopwatch = Stopwatch.StartNew();
        if (request.Format == "pdf")
        {
            foreach (var group in (options.Combine ?? true) ? new[] { selected } : selected.Select(view => new List<View> { view }))
            {
                var name = planned[(options.Combine ?? true) ? 0 : selected.IndexOf(group[0])];
                using var pdf = new PDFExportOptions
                {
                    Combine = true,
                    FileName = Path.GetFileNameWithoutExtension(name),
                    ColorDepth = options.Color switch
                    {
                        "grayscale" => ColorDepthType.GrayScale,
                        "black_line" => ColorDepthType.BlackLine,
                        _ => ColorDepthType.Color
                    },
                    ZoomType = ZoomType.Zoom,
                    ZoomPercentage = options.ZoomPercent ?? 100,
                    HideCropBoundaries = options.HideCropBoundaries ?? true,
                    HideScopeBoxes = options.HideScopeBoxes ?? true
                };
                if (!document.Export(folder, group.Select(view => view.Id).ToList(), pdf))
                    throw new InvalidOperationException($"PDF export failed for '{name}'.");
            }
        }
        else if (request.Format == "dwg")
        {
            var setup = options.Setup;
            using var dwg = setup is null ? new DWGExportOptions() : DWGExportOptions.GetPredefinedOptions(document, setup)
                ?? throw new ArgumentException($"DWG setup '{setup}' was not found.");
            dwg.MergedViews = options.MergedViews ?? false;
            if (options.FileVersion is not null)
            {
                if (!Enum.TryParse<ACADVersion>(options.FileVersion, true, out var version) || !Enum.IsDefined(typeof(ACADVersion), version))
                    throw new ArgumentException("file_version is invalid.");
                dwg.FileVersion = version;
            }
            for (var index = 0; index < selected.Count; index++)
                if (!document.Export(folder, Path.GetFileNameWithoutExtension(planned[index]), [selected[index].Id], dwg))
                    throw new InvalidOperationException($"DWG export failed for '{planned[index]}'.");
        }
        else if (request.Format == "ifc")
        {
            using var ifc = new IFCExportOptions
            {
                FileVersion = Enum.Parse<IFCVersion>(options.Version ?? "IFC2x3CV2"),
                FilterViewId = selected.Count == 1 ? selected[0].Id : ElementId.InvalidElementId,
                ExportBaseQuantities = options.ExportBaseQuantities ?? true,
                SpaceBoundaryLevel = options.SpaceBoundaries ?? 0,
                WallAndColumnSplitting = options.SplitWallsByLevel ?? false
            };
            using var transaction = new Transaction(document, "MCP IFC export");
            transaction.Start();
            try
            {
                if (!document.Export(folder, Path.GetFileNameWithoutExtension(planned[0]), ifc)) throw new InvalidOperationException("IFC export failed.");
            }
            finally { transaction.RollBack(); }
        }
        else
        {
            for (var index = 0; index < selected.Count; index++)
            {
                var schedule = (ViewSchedule)selected[index];
                using var csvOptions = new ViewScheduleExportOptions
                {
                    FieldDelimiter = options.Delimiter ?? ",",
                    TextQualifier = ExportTextQualifier.DoubleQuote,
                    HeadersFootersBlanks = options.GroupHeaders ?? false,
                    Title = options.Title ?? false,
                    ColumnHeaders = options.Headers == false ? ExportColumnHeaders.None :
                        options.GroupHeaders == true ? ExportColumnHeaders.MultipleRows : ExportColumnHeaders.OneRow
                };
                schedule.Export(folder, planned[index], csvOptions);
                var path = Path.Combine(folder, planned[index]);
                using var reader = new StreamReader(path, Encoding.Default, true);
                var contents = reader.ReadToEnd();
                reader.Close();
                File.WriteAllText(path, contents, new UTF8Encoding(true));
            }
        }
        foreach (var file in result.Files!)
        {
            var path = Path.Combine(folder, file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidOperationException($"Export did not produce '{file.Name}'.");
            file.SizeBytes = new FileInfo(path).Length;
        }
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
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
        bool wrapGroup = true)
    {
        viewOpened = false;
        if (command == "undo-last") return ExecuteUndoLast(document, uiDocument);
        if (command == "export-nwc")
        {
            var exportResult = NwcExporter.Execute(document, action);
            if (exportResult.NeedsConfirmation != true)
                exportResult.Summary = BuildSummary(command, action, exportResult, document.Title, null);
            return exportResult;
        }
        if (command == "export") return ExportFiles(document, action);
        if (command == "align-link-datums")
            return AlignLinkDatums.Execute(document, action.DatumOptions!, action.DryRun, failures, clientName);
        if (command == "set-view-visibility")
            return ViewVisibility.Execute(document, action.Visibility!, action.DryRun, failures, clientName);
        if (command == "remove-links")
            return LinkRemoval.Execute(document, action.LinkRemoval!, action.DryRun, failures, clientName, action.ConfirmToken);
        if (command is "select" or "show" or "isolate" && uiDocument is null)
            throw new InvalidOperationException($"Cannot run '{command}' on '{document.Title}' because it is not the active document; activate it in Revit first.");
        var ids = command == "isolate" && action.Reset ? [] : ResolveIds(document, action.ElementIds);
        if (command is "select" or "show")
        {
            if (command == "show")
            {
                viewOpened = OpenViewForElements(uiDocument!, ids);
                uiDocument!.ShowElements(ids);
                if (viewOpened) DocumentActions.ZoomActiveView(uiDocument);
            }
            if (command == "select" || action.Select) uiDocument!.Selection.SetElementIds(ids);
            var viewData = new ActionResultData { Count = uiDocument!.Selection.GetElementIds().Count };
            viewData.Summary = BuildSummary(command, action, viewData, document.Title, ids);
            return viewData;
        }

        var skips = new SkippedByReason();
        var transform = TransformSkipPolicy.Supports(command, action.Copy);
        var requestedCount = ids.Count;
        if (transform)
        {
            var free = new List<ElementId>();
            foreach (var id in ids)
            {
                var element = document.GetElement(id)!;
                var reason = TransformSkipPolicy.ClassifyBefore(element.Pinned, element.GroupId != ElementId.InvalidElementId);
                if (reason is null) free.Add(id);
                else TransformSkipPolicy.Add(skips, reason.Value, RevitValueReader.GetId(id));
            }
            if (free.Count == 0) throw new InvalidOperationException(TransformSkipPolicy.NoneChangedMessage(command, requestedCount, skips));
            ids = free;
            action.ElementIds = ids.Select(RevitValueReader.GetId).ToList();
        }
        var preflight = ActionPreflight.Run(document, command, action, ids);
        if (GroupSkipPolicy.Supports(command) && preflight is { InGroup.Count: > 0 })
        {
            skips.InGroup.AddRange(preflight.InGroup.Where(id => !skips.InGroup.Contains(id)));
            ids = ids.Where(id => !preflight.InGroup.Contains(RevitValueReader.GetId(id))).ToList();
            action.ElementIds = ids.Select(RevitValueReader.GetId).ToList();
        }
        skips.InGroup.Sort();
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
                var poses = transform ? ids.Select(id => ActionVerifier.Pose(document.GetElement(id)!)).ToList() : null;
                var data = Mutate(document, command, action, ids, preflight);
                var changedIds = ids;
                if (poses is not null)
                {
                    document.Regenerate();
                    changedIds = [];
                    for (var index = 0; index < ids.Count; index++)
                    {
                        var element = document.GetElement(ids[index])!;
                        if (!TransformSkipPolicy.SamePose(poses[index], ActionVerifier.Pose(element)))
                            changedIds.Add(ids[index]);
                        else
                            TransformSkipPolicy.Add(skips, TransformSkipPolicy.ClassifyUnchanged(ActionVerifier.IsHosted(element)),
                                RevitValueReader.GetId(ids[index]));
                    }
                    if (changedIds.Count == 0)
                        throw new InvalidOperationException(TransformSkipPolicy.NoneChangedMessage(command, requestedCount, skips));
                    data.Count = changedIds.Count;
                    data.Verification = new ActionVerification { Changed = changedIds.Select(RevitValueReader.GetId).ToList() };
                }
                if (TransformSkipPolicy.Total(skips) > 0)
                {
                    data.Skipped = skips;
                    var warning = TransformSkipPolicy.Warning(skips);
                    data.Warning = data.Warning is null ? warning : data.Warning + " " + warning;
                }
                data.DryRun = action.DryRun;
                data.Verification ??= new ActionVerification();
                data.Verification.Before = before;
                if (command == "delete")
                    before!.Dependents = data.Verification.Changed!.Except(before.Requested!).ToList();
                if (command == "delete" && !action.DryRun && DeletionPolicy.RequiresConfirmation(data.Verification.Changed!.Count))
                {
                    var deletedCount = data.Verification.Changed.Count;
                    if (!wrapGroup) throw new InvalidOperationException(DeletionPolicy.BatchRefusal(deletedCount));
                    var identity = DocumentConfirmationBinding.Identity(document.PathName, document.Title);
                    var deleteArguments = DocumentConfirmationBinding.DeleteArguments(before!.Requested!, deletedCount);
                    const string changedMessage = "The document changed after the preview. The confirmation token is used up; repeat the call without confirm_token to get a new one.";
                    if (action.ConfirmToken is null)
                    {
                        if (transaction.RollBack() != TransactionStatus.RolledBack)
                            throw new InvalidOperationException(failures.Message ?? "Could not roll back the deletion preview.");
                        group?.RollBack();
                        return ConfirmationStore.Gate("delete", identity, deleteArguments, ConfirmationStore.State(document), null,
                            $"Delete {deletedCount} elements ({before.Requested!.Count} requested, {before.Dependents!.Count} dependents) from {document.Title}. One undo entry restores them.",
                            $"Needs confirmation to delete {deletedCount} elements from {document.Title}.", changedMessage)!;
                    }
                    ConfirmationStore.Gate("delete", identity, deleteArguments, ConfirmationStore.State(document),
                        action.ConfirmToken, string.Empty, string.Empty, changedMessage);
                }
                document.Regenerate();
                ActionVerifier.CaptureAfter(document, command, action, data);
                CaptureViewSheetAfter(document, command, action, data);
                if (action.DryRun && command == "set-parameter")
                    data.Verification.Changed = [action.ElementId];
                data.Summary = BuildSummary(command, action, data, document.Title, changedIds);
                if (action.DryRun)
                {
                    // Dry runs must never commit: some deletions are irreversible.
                    if (transaction.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException(failures.Message ?? "Could not roll back the dry run.");
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
                        CaptureViewSheetAfter(document, command, action, data);
                        if (command == "place-families" && data.CreatedElementIds!.Any(id => document.GetElement(CreateId(id)) is null))
                            throw new InvalidOperationException("Post-commit verification found a missing family instance.");
                    }
                    catch (Exception exception)
                    {
                        data.Verification.Error = "Post-commit verification failed: " + exception.Message;
                        PluginLog.Error(data.Verification.Error, exception);
                    }
                    if (group is not null)
                    {
                        var undoSummary = command == "place-families" ? $"Place {data.Placed} families" : data.Summary;
                        var groupName = ActionSummaryBuilder.BuildGroupName(clientName, undoSummary!);
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
        var newest = ActivityLog.NewestUndoable();
        if (newest?.Command == "remove-links" && newest.UndoEntryName is not null)
            throw new InvalidOperationException(LinkRemoval.UndoWarning);
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
                Command = "undo-last",
                DocumentTitle = document.Title
            })
        };
    }

    private static string BuildSummary(string command, ActionJobContract action, ActionResultData data,
        string documentTitle, List<ElementId>? ids)
    {
        var count = command switch
        {
            "move" or "rotate" or "change-type" or "select" or "isolate" or "override-graphics" => ids?.Count ?? 0,
            "copy" or "mirror" or "update-parameters" => data.Count ?? 0,
            "show" => data.Count ?? ids?.Count ?? 0,
            "delete" => data.Verification?.Changed?.Count ?? ids?.Count ?? 0,
            "load-family" or "place-families" or "create-mep-run" => data.Count ?? 0,
            "walls-from-cad" => data.Count ?? 0,
            "apply-view-template" or "place-views-on-sheet" => data.Count ?? 0,
            _ => 0
        };
        return ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
        {
            Command = command,
            DocumentTitle = documentTitle,
            Count = count,
            DryRun = action.DryRun,
            Reset = action.Reset,
            Family = action.Family,
            TypeName = action.TypeName,
            Parameter = action.Parameter,
            WallType = action.WallType,
            CadLink = action.CadLink,
            ViewName = data.ViewName ?? data.SheetName ?? action.Name ?? action.View ?? action.Sheet,
            ViewKind = action.Kind,
            SheetNumber = data.SheetNumber ?? action.Number,
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
            ViewName = data.View?.Name,
            Count = data.ClosedViews?.Count ?? 0,
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

    private static void CaptureViewSheetAfter(Document document, string command, ActionJobContract action, ActionResultData result)
    {
        var verification = result.Verification!;
        switch (command)
        {
            case "create-view":
            case "duplicate-view":
            case "create-sheet":
                var created = document.GetElement(CreateId(result.Id!.Value))
                    ?? throw new InvalidOperationException($"Verification could not find element {result.Id.Value}.");
                verification.After = new ActionFacts { Id = RevitValueReader.GetId(created.Id), Category = created.Category?.Name };
                verification.WouldCreate = action.DryRun ? true : null;
                break;
            case "apply-view-template":
                var affected = action.Views!.Select(reference => ReadCommandReader.FindView(document, reference)
                    ?? throw new InvalidOperationException($"View '{reference}' was not found after applying template.")).ToList();
                verification.After = new ActionFacts { Elements = affected.Select(view => new ActionFacts { Id = RevitValueReader.GetId(view.Id) }).ToList() };
                break;
            case "place-views-on-sheet":
                verification.After = new ActionFacts
                {
                    Elements = verification.Changed!.Select(id =>
                {
                    var element = document.GetElement(CreateId(id))
                        ?? throw new InvalidOperationException($"Verification could not find element {id}.");
                    return new ActionFacts { Id = RevitValueReader.GetId(element.Id) };
                }).ToList()
                };
                break;
        }
    }

    private static ActionResultData Mutate(Document document, string command, ActionJobContract action, List<ElementId> ids, PreflightState? preflight)
    {
        switch (command)
        {
            case "isolate":
                if (action.Reset) document.ActiveView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                else document.ActiveView.IsolateElementsTemporary(ids);
                return new ActionResultData { Count = ids.Count };
            case "override-graphics":
                return ActionMutations.OverrideGraphics(document, action, ids);
            case "move":
                return ActionMutations.Move(document, action, ids);
            case "rotate":
                return ActionMutations.Rotate(document, action, ids);
            case "copy":
                return ActionMutations.Copy(document, action, ids);
            case "mirror":
                return ActionMutations.Mirror(document, action, ids);
            case "change-type":
                return ActionMutations.ChangeType(document, action, ids);
            case "update-parameters":
                return ActionMutations.UpdateParameters(document, action, preflight?.InGroup);
            case "delete":
                var deleted = document.Delete(ids).Select(RevitValueReader.GetId).OrderBy(value => value).ToList();
                return new ActionResultData
                {
                    Count = deleted.Count,
                    Verification = new ActionVerification { Changed = deleted }
                };
            case "place-family":
                return ActionMutations.PlaceFamily(document, action);
            case "load-family":
                return ActionMutations.LoadFamilies(document, action);
            case "place-families":
                return ActionMutations.PlaceFamilies(document, action);
            case "create-wall":
                return ActionMutations.CreateWall(document, action);
            case "create-mep-run":
                return ActionMutations.CreateMepRun(document, action);
            case "link-cad":
                return ActionMutations.LinkCad(document, action);
            case "walls-from-cad":
                return ActionMutations.WallsFromCad(document, action, preflight);
            case "set-parameter":
                return ActionMutations.SetParameter(document, action);
            case "create-view":
                return ActionMutations.CreateView(document, action);
            case "duplicate-view":
                return ActionMutations.DuplicateView(document, action);
            case "apply-view-template":
                return ActionMutations.ApplyViewTemplate(document, action);
            case "create-sheet":
                return ActionMutations.CreateSheet(document, action);
            case "place-views-on-sheet":
                return ActionMutations.PlaceViewsOnSheet(document, action);
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
            Message = errors.Count > 0 ? RepeatedMessages.Join(errors) : null;
            if (rollBack) return FailureProcessingResult.ProceedWithRollBack;
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }
}
