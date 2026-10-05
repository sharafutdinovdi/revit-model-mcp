using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Export;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal static class ReadCommandExecutor
{
    private const long MaximumFastCommandDurationMs = 60_000;

    public static void Execute(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        SkippedReadDiagnostics.Current = new SkippedReadDiagnostics();
        var output = CommandResponseFileWriter.Create(
            startedAt.LocalDateTime,
            job.Command,
            ReadCommandReader.ReadResponder(application),
            job.CorrelationId);
        output.Write(CommandResponse<string>.PartialResult(
            job.Command,
            "accepted",
            "Command accepted and running.",
            0));
        output.Log(job.Command, "accepted", 0, 0, 0, null);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (job.Kind == ControlJobKind.Ping)
            {
                stopwatch.Stop();
                output.Write(ReadCommandResponseFactory.Ping(stopwatch.ElapsedMilliseconds));
                LogFinished(job.Command, "success", stopwatch.ElapsedMilliseconds, output.FilePath, null);
                return;
            }

            if (job.Command == "family-audit")
            {
                var reference = job.CoordinatorJob.TargetDocument;
                var target = ActionCommandExecutor.ResolveDocument(application,
                    string.IsNullOrWhiteSpace(reference) ? null : reference!.Trim());
                ActionJobParser.ValidateFamilyMode(job.Action ?? throw new ArgumentException("Missing family arguments."), target.IsFamilyDocument);
                var audit = FamilyAuditReader.Read(target, job.Action?.Families);
                stopwatch.Stop();
                output.Write(CommandResponse<FamilyAuditData>.Ok(job.Command, audit, stopwatch.ElapsedMilliseconds));
                LogFinished(job.Command, "success", stopwatch.ElapsedMilliseconds, output.FilePath, null);
                return;
            }
            if (job.Kind == ControlJobKind.Documents)
            {
                WriteSuccess(output, job.Command, DocumentActions.List(application, job.IncludeLinked), stopwatch);
                return;
            }
            if (job.Kind == ControlJobKind.UiState)
            {
                WriteSuccess(output, job.Command, DocumentActions.UiState(application), stopwatch);
                return;
            }
            if (job.Kind == ControlJobKind.NwcSettingsCheck)
            {
                var settingsPath = NwcPathValidator.EnsureAbsoluteNoTraversal(job.CoordinatorJob.SettingsXml!, "settings_xml");
                var xml = NwcSettingsXml.ReadFile(settingsPath);
                WriteSuccess(output, job.Command, xml, stopwatch);
                return;
            }
            var document = job.Kind is ControlJobKind.ViewInfo or ControlJobKind.ModelSnapshot or ControlJobKind.ScheduleData or ControlJobKind.CaptureElements
                ? ActionCommandExecutor.ResolveDocument(application, job.TargetDocument)
                : application.ActiveUIDocument?.Document
                    ?? throw new InvalidOperationException("No active Revit document.");
            if (job.Command == "compare-link-datums")
            {
                WriteSuccess(output, job.Command, LinkDatumReader.Read(document, job.Action!.DatumOptions!), stopwatch);
                return;
            }
            switch (job.Kind)
            {
                case ControlJobKind.ModelSnapshot:
                    var snapshot = ModelSnapshotReader.Read(document, job.ParameterRules);
                    SkippedReadDiagnostics.Current = new SkippedReadDiagnostics();
                    WriteSuccess(output, job.Command, snapshot, stopwatch);
                    break;
                case ControlJobKind.ModelHealth:
                    WriteSuccess(output, job.Command, ModelHealthReader.Read(document, job.CoordinatorJob), stopwatch);
                    break;
                case ControlJobKind.LinksStatus:
                    WriteSuccess(output, job.Command, LinksStatusReader.Read(document, job.CoordinatorJob), stopwatch);
                    break;
                case ControlJobKind.SharedCoordinates:
                    WriteSuccess(output, job.Command, SharedCoordinatesReader.Read(document, job.CoordinatorJob), stopwatch);
                    break;
                case ControlJobKind.ParameterFillCheck:
                    WriteSuccess(output, job.Command, ParameterFillReader.Read(document, job.CoordinatorJob), stopwatch);
                    break;
                case ControlJobKind.DocumentInfo:
                    WriteSuccess(output, job.Command, ReadCommandReader.ReadDocumentInfo(application), stopwatch);
                    break;
                case ControlJobKind.ListViews:
                    var views = ReadCommandReader.ReadViews(
                        document,
                        job.ViewType,
                        job.NameContains,
                        (partial, currentView, elapsedMs) =>
                            WriteListViewsProgress(
                                output,
                                job.Command,
                                partial,
                                currentView,
                                elapsedMs));
                    if (views.Processed < views.Total)
                    {
                        stopwatch.Stop();
                        output.Write(CommandResponse<ViewListData>.PartialResult(
                            job.Command,
                            views,
                            $"The 60-second limit was reached. Processed {views.Processed} of {views.Total} views.",
                            stopwatch.ElapsedMilliseconds));
                        LogFinished(
                            job.Command,
                            "partial",
                            stopwatch.ElapsedMilliseconds,
                            output.FilePath,
                            $"Processed={views.Processed}. Total={views.Total}.");
                    }
                    else
                    {
                        WriteSuccess(
                            output,
                            job.Command,
                            views,
                            stopwatch,
                            "View elements were not read; use view-summary to inspect their composition.");
                    }

                    break;
                case ControlJobKind.ViewSummary:
                    ExecuteForView(output, document, job, stopwatch, ReadCommandReader.ReadViewSummary);
                    break;
                case ControlJobKind.ViewInfo:
                    var infoView = ViewInfoReader.FindView(document, job.View!);
                    if (infoView is null)
                        WriteFailure<object>(output, job.Command, $"View '{job.View}' was not found.", stopwatch);
                    else
                        WriteSuccess(output, job.Command, ViewInfoReader.Read(document, infoView), stopwatch);
                    break;
                case ControlJobKind.ScheduleData:
                    WriteSuccess(output, job.Command, ReadSchedule(document, job.View!, job.Offset, job.Limit), stopwatch);
                    break;
                case ControlJobKind.ElementDetails:
                    ExecuteElementDetails(output, document, job, stopwatch);
                    break;
                case ControlJobKind.ViewWarnings:
                    ExecuteForView(
                        output,
                        document,
                        job,
                        stopwatch,
                        ReadCommandReader.ReadViewWarnings,
                        "Matching uses the elements involved in warnings.");
                    break;
                case ControlJobKind.CaptureElements:
                    WriteSuccess(output, job.Command, CaptureElements(document, job, startedAt.LocalDateTime), stopwatch);
                    break;
                case ControlJobKind.ExportView:
                    ExecuteExportView(output, document, job, stopwatch, startedAt.LocalDateTime);
                    break;
                case ControlJobKind.QueryElements:
                    WriteSuccess(output, job.Command, ElementQueryReader.ReadQuery(document, job), stopwatch);
                    break;
                case ControlJobKind.AggregateElements:
                    WriteSuccess(output, job.Command, ElementQueryReader.ReadAggregate(document, job), stopwatch);
                    break;
                case ControlJobKind.ListCatalog:
                    WriteSuccess(output, job.Command, CatalogReader.Read(document, job.CatalogSection!), stopwatch);
                    break;
                case ControlJobKind.ListWarnings:
                    WriteSuccess(
                        output,
                        job.Command,
                        ModelWarningReader.Read(document, job.WarningText, job.IncludeElements),
                        stopwatch);
                    break;
                case ControlJobKind.ListRelations:
                    WriteSuccess(output, job.Command, RelationReader.Read(document, job), stopwatch);
                    break;
                default:
                    WriteFailure<object>(output, job.Command, "The command is not a fast read command.", stopwatch);
                    break;
            }
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            PluginLog.Error($"Job processing failed. Command='{job.Command}'.", exception);
            output.Write(CommandResponse<object>.Fail(
                job.Command,
                $"Failed to execute the command: {exception.Message}",
                stopwatch.ElapsedMilliseconds));
            LogFinished(job.Command, "error", stopwatch.ElapsedMilliseconds, output.FilePath, exception.Message);
        }
        finally
        {
            SkippedReadDiagnostics.Current = null;
        }
    }

    internal static ScheduleDataResult ReadSchedule(Document document, string reference, int offset, int maxRows)
    {
        var view = ReadCommandReader.FindView(document, reference);
        if (view is null)
            throw new ArgumentException($"Schedule '{reference}' was not found.");
        if (view is not ViewSchedule schedule || schedule.IsTemplate)
            throw new ArgumentException($"'{reference}' is not a schedule.");
        var table = schedule.GetTableData();
        var body = table.GetSectionData(SectionType.Body);
        var visibleFields = schedule.Definition.GetFieldOrder()
            .Select(schedule.Definition.GetField).Where(field => !field.IsHidden).ToList();
        var columnNumbers = new List<int>();
        for (var column = body.FirstColumnNumber; column <= body.LastColumnNumber; column++)
        {
            try
            {
                if (body.FirstRowNumber <= body.LastRowNumber)
                    schedule.GetCellText(SectionType.Body, body.FirstRowNumber, column);
                columnNumbers.Add(column);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        }
        if (columnNumbers.Count > visibleFields.Count)
            columnNumbers = columnNumbers.Take(visibleFields.Count).ToList();

        var dataStart = body.FirstRowNumber;
        var columns = visibleFields.Take(columnNumbers.Count).Select(field => field.ColumnHeading).ToList();
        if (schedule.Definition.ShowHeaders && body.FirstRowNumber <= body.LastRowNumber)
        {
            var headingRows = new List<List<string>>();
            for (var row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
            {
                var hasMergedCells = false;
                var headings = new List<string>();
                foreach (var column in columnNumbers)
                {
                    var merged = body.GetMergedCell(row, column);
                    hasMergedCells |= merged.Top == row &&
                        (merged.Right > merged.Left || merged.Bottom > merged.Top);
                    headings.Add(merged.Top == row
                        ? schedule.GetCellText(SectionType.Body, merged.Top, merged.Left)
                        : string.Empty);
                }
                headingRows.Add(headings);
                dataStart = row + 1;
                if (!hasMergedCells) break;
            }
            columns = ScheduleDataResult.JoinHeadings(headingRows, columnNumbers.Count);
        }
        var totalRows = Math.Max(0, body.LastRowNumber - dataStart + 1);
        var rows = new List<List<string>>();
        for (var row = dataStart + offset; row <= body.LastRowNumber && rows.Count < maxRows; row++)
            rows.Add(columnNumbers.Select(column => schedule.GetCellText(SectionType.Body, row, column)).ToList());
        return new ScheduleDataResult
        {
            Columns = columns,
            Rows = rows,
            TotalRows = totalRows,
            Truncated = offset + rows.Count < totalRows
        };
    }

    public static void WriteInvalid(
        UIApplication application,
        ControlJobParseResult job,
        DateTimeOffset startedAt)
    {
        WriteError(application, job.Command, job.Error ?? "Invalid job.", startedAt, job.CorrelationId);
    }

    public static void WriteError(
        UIApplication application,
        string command,
        string message,
        DateTimeOffset startedAt,
        string? correlationId = null)
    {
        if (ActionJobParser.IsAction(command))
        {
            ActionCommandExecutor.WriteError(application, command, message, startedAt, correlationId);
            return;
        }
        var output = CommandResponseFileWriter.Create(
            startedAt.LocalDateTime,
            command,
            ReadCommandReader.ReadResponder(application),
            correlationId);
        output.Write(CommandResponse<object>.Fail(command, message, 0));
        LogFinished(command, "error", 0, output.FilePath, message);
    }

    private static void ExecuteForView<T>(
        CommandResponseFileWriter output,
        Document document,
        ControlJobParseResult job,
        Stopwatch stopwatch,
        Func<Document, View, T> read,
        string? message = null)
    {
        var view = ReadCommandReader.FindView(document, job.View!);
        if (view is null)
        {
            stopwatch.Stop();
            output.Write(CommandResponse<T>.ViewNotFound(
                job.Command,
                job.View!,
                stopwatch.ElapsedMilliseconds));
            LogFinished(job.Command, "error", stopwatch.ElapsedMilliseconds, output.FilePath, $"View='{job.View}'.");
            return;
        }

        WriteSuccess(output, job.Command, read(document, view), stopwatch, message);
    }

    private static bool WriteListViewsProgress(
        CommandResponseFileWriter output,
        string command,
        ViewListData data,
        string currentView,
        long elapsedMs)
    {
        var timedOut = elapsedMs >= MaximumFastCommandDurationMs;
        var shouldReport = data.Processed == 0 || data.Processed >= data.Total || data.Processed % 25 == 0 || timedOut;
        if (!shouldReport)
        {
            return !timedOut;
        }

        var state = currentView == "<collector-start>"
            ? "collector-start"
            : timedOut
                ? "timeout"
                : data.Processed >= data.Total
                    ? "metadata-read"
                    : "processing";
        output.Log(command, state, data.Processed, data.Total, elapsedMs, currentView);
        var current = string.IsNullOrWhiteSpace(currentView) ? string.Empty : $" Current view: '{currentView}'.";
        output.Write(CommandResponse<ViewListData>.PartialResult(
            command,
            data,
            $"Processed {data.Processed} of {data.Total} views.{current} View elements were not read.",
            elapsedMs));
        return !timedOut;
    }

    private static void ExecuteElementDetails(
        CommandResponseFileWriter output,
        Document document,
        ControlJobParseResult job,
        Stopwatch stopwatch)
    {
        var id = CreateElementId(job.ElementId!.Value);
        var element = id is null ? null : document.GetElement(id);
        if (element is null)
        {
            stopwatch.Stop();
            output.Write(CommandResponse<ElementDetailsData>.ElementNotFound(
                job.Command,
                job.ElementId.Value,
                stopwatch.ElapsedMilliseconds));
            LogFinished(job.Command, "error", stopwatch.ElapsedMilliseconds, output.FilePath, $"ElementId={job.ElementId.Value}.");
            return;
        }

        var warningIds = ReadCommandReader.ReadWarningElementIds(document);
        var reader = new ViewElementReader(document, warningIds);
        WriteSuccess(output, job.Command, reader.ReadDetails(element), stopwatch);
    }

    private static ElementCaptureData CaptureElements(Document document, ControlJobParseResult job, DateTime localTime)
    {
        var elements = new List<Element>();
        var missing = new List<long>();
        foreach (var value in job.ElementIds)
        {
            var id = CreateElementId(value);
            var element = id is null ? null : document.GetElement(id);
            if (element is null || element is ElementType || element.Category?.CategoryType != CategoryType.Model ||
                element.get_BoundingBox(null) is null)
                missing.Add(value);
            else elements.Add(element);
        }
        if (elements.Count == 0)
            throw new ArgumentException("No model elements with bounding boxes remain in elementIds.");
        var ids = elements.Select(element => element.Id).ToHashSet();
        var bounds = ActionMutations.ResolveBox(document, new ActionJobContract
        {
            ElementIds = elements.Select(element => RevitValueReader.GetId(element.Id)).ToList()
        }, job.PaddingMm);
        using var group = new TransactionGroup(document, "Capture elements");
        group.Start();
        try
        {
            View view;
            using (var transaction = new Transaction(document, "Prepare element snapshot"))
            {
                transaction.Start();
                if (job.Mode == "3d")
                {
                    using var types = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType));
                    var type = types.Cast<ViewFamilyType>().FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional)
                        ?? throw new InvalidOperationException("No 3D view family type is available.");
                    var created = View3D.CreateIsometric(document, type.Id);
                    var height = bounds.Max.Z - bounds.Min.Z;
                    var width = Math.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Y - bounds.Min.Y);
                    if (width < height)
                    {
                        var horizontalPadding = (height - width) / 2;
                        bounds.Min = new XYZ(bounds.Min.X - horizontalPadding, bounds.Min.Y - horizontalPadding, bounds.Min.Z);
                        bounds.Max = new XYZ(bounds.Max.X + horizontalPadding, bounds.Max.Y + horizontalPadding, bounds.Max.Z);
                    }
                    created.SetSectionBox(bounds);
                    created.IsSectionBoxActive = true;
                    view = created;
                }
                else
                {
                    using var plans = new FilteredElementCollector(document).OfClass(typeof(ViewPlan));
                    var eligible = plans.Cast<ViewPlan>().Where(plan => !plan.IsTemplate && plan.ViewType == ViewType.FloorPlan).ToList();
                    var level = elements.Select(element => element.LevelId).FirstOrDefault(id => id != ElementId.InvalidElementId);
                    var source = eligible.FirstOrDefault(plan => plan.GenLevel?.Id == level) ?? eligible.FirstOrDefault()
                        ?? throw new InvalidOperationException("No floor plan is available for an element snapshot.");
                    view = (View)document.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate));
                    view.ViewTemplateId = ElementId.InvalidElementId;
                    var crop = view.CropBox;
                    var inverse = crop.Transform.Inverse;
                    var corners = new List<XYZ>();
                    foreach (var coordinateX in new[] { bounds.Min.X, bounds.Max.X })
                        foreach (var coordinateY in new[] { bounds.Min.Y, bounds.Max.Y })
                            foreach (var coordinateZ in new[] { bounds.Min.Z, bounds.Max.Z })
                                corners.Add(inverse.OfPoint(new XYZ(coordinateX, coordinateY, coordinateZ)));
                    crop.Min = new XYZ(corners.Min(point => point.X), corners.Min(point => point.Y), crop.Min.Z);
                    crop.Max = new XYZ(corners.Max(point => point.X), corners.Max(point => point.Y), crop.Max.Z);
                    view.CropBox = crop;
                    view.CropBoxActive = true;
                    view.CropBoxVisible = false;
                }
                view.DetailLevel = ViewDetailLevel.Fine;
                view.DisplayStyle = DisplayStyle.ShadingWithEdges;
                document.Regenerate();
                using var patterns = new FilteredElementCollector(document).OfClass(typeof(FillPatternElement));
                var solid = patterns.Cast<FillPatternElement>().FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill)
                    ?? throw new InvalidOperationException("The document has no solid fill pattern.");
                var red = new Color(255, 0, 0);
                using var settings = new OverrideGraphicSettings();
                settings.SetSurfaceForegroundPatternId(solid.Id);
                settings.SetSurfaceForegroundPatternColor(red);
                settings.SetCutForegroundPatternId(solid.Id);
                settings.SetCutForegroundPatternColor(red);
                settings.SetProjectionLineColor(red);
                settings.SetCutLineColor(red);
                settings.SetSurfaceTransparency(0);
                settings.SetProjectionLineWeight(6);
                settings.SetCutLineWeight(6);
                foreach (var id in ids) view.SetElementOverrides(id, settings);
                using var visible = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType();
                foreach (var id in visible.ToElementIds())
                {
                    if (ids.Contains(id) || document.GetElement(id)?.Category?.CategoryType != CategoryType.Model) continue;
                    using var other = view.GetElementOverrides(id);
                    other.SetSurfaceTransparency(60);
                    ActionMutations.SetHalftone(view, id, other);
                }
                document.Regenerate();
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("The temporary snapshot view could not be prepared.");
            }
            var image = ViewImageExporter.Export(document, view, job.PixelSize, true, localTime);
            return new ElementCaptureData
            {
                FileName = image.FileName,
                Width = image.Width,
                Height = image.Height,
                SizeBytes = image.SizeBytes,
                ElementCount = elements.Count,
                MissingIds = missing,
                Mode = job.Mode
            };
        }
        finally
        {
            group.RollBack();
        }
    }

    private static void ExecuteExportView(
        CommandResponseFileWriter output,
        Document document,
        ControlJobParseResult job,
        Stopwatch stopwatch,
        DateTime localTime)
    {
        var view = ReadCommandReader.FindView(document, job.View!);
        if (view is null)
        {
            stopwatch.Stop();
            output.Write(CommandResponse<ViewExportData>.ViewNotFound(
                job.Command,
                job.View!,
                stopwatch.ElapsedMilliseconds));
            LogFinished(job.Command, "error", stopwatch.ElapsedMilliseconds, output.FilePath, $"View='{job.View}'.");
            return;
        }

        try
        {
            WriteSuccess(
                output,
                job.Command,
                ViewImageExporter.Export(document, view, job.PixelSize, job.ZoomToFit, localTime),
                stopwatch);
        }
        catch (InvalidOperationException exception)
        {
            WriteFailure<ViewExportData>(output, job.Command, exception.Message, stopwatch);
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException exception)
        {
            WriteFailure<ViewExportData>(
                output,
                job.Command,
                $"Failed to export view '{view.Name}' of type {view.ViewType}: {exception.Message}",
                stopwatch);
        }
        catch (System.IO.IOException exception)
        {
            WriteFailure<ViewExportData>(output, job.Command, exception.Message, stopwatch);
        }
    }

    private static ElementId? CreateElementId(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return value > int.MaxValue ? null : new ElementId((int)value);
#endif
    }

    private static void WriteSuccess<T>(
        CommandResponseFileWriter output,
        string command,
        T data,
        Stopwatch stopwatch,
        string? message = null)
    {
        stopwatch.Stop();
        if (stopwatch.ElapsedMilliseconds >= MaximumFastCommandDurationMs)
        {
            var timeoutMessage = string.IsNullOrWhiteSpace(message)
                ? "The 60-second limit was reached; the result is marked as partial."
                : $"{message} The 60-second limit was reached; the result is marked as partial.";
            output.Write(CommandResponse<T>.PartialResult(
                command,
                data,
                timeoutMessage,
                stopwatch.ElapsedMilliseconds));
            LogFinished(command, "partial", stopwatch.ElapsedMilliseconds, output.FilePath, timeoutMessage);
            return;
        }

        if (stopwatch.ElapsedMilliseconds >= 2_000)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? "The command took more than two seconds; elapsedMs reports the duration."
                : $"{message} The command took more than two seconds; elapsedMs reports the duration.";
        }

        output.Write(CommandResponse<T>.Ok(command, data, stopwatch.ElapsedMilliseconds, message));
        LogFinished(command, "success", stopwatch.ElapsedMilliseconds, output.FilePath, message);
    }

    private static void WriteFailure<T>(
        CommandResponseFileWriter output,
        string command,
        string message,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        output.Write(CommandResponse<T>.Fail(command, message, stopwatch.ElapsedMilliseconds));
        LogFinished(command, "error", stopwatch.ElapsedMilliseconds, output.FilePath, message);
    }

    private static void LogFinished(
        string command,
        string outcome,
        long elapsedMs,
        string responsePath,
        string? message)
    {
        PluginLog.Info(
            $"Job processing finished. Command='{command}'. Outcome='{outcome}'. ElapsedMs={elapsedMs}. " +
            $"ResponsePath='{responsePath}'. Message='{message ?? string.Empty}'.");
    }
}
