using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal static class ReadCommandExecutor
{
    private const long MaximumFastCommandDurationMs = 60_000;

    public static void Execute(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        var output = CommandResponseFileWriter.Create(
            startedAt.LocalDateTime,
            job.Command,
            ReadCommandReader.ReadResponder(application));
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

            var document = application.ActiveUIDocument?.Document
                           ?? throw new InvalidOperationException("No active Revit document.");
            switch (job.Kind)
            {
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
                $"Failed to execute the command: {exception}",
                stopwatch.ElapsedMilliseconds));
            LogFinished(job.Command, "error", stopwatch.ElapsedMilliseconds, output.FilePath, exception.Message);
        }
    }

    public static void WriteInvalid(
        UIApplication application,
        ControlJobParseResult job,
        DateTimeOffset startedAt)
    {
        WriteError(application, job.Command, job.Error ?? "Invalid job.", startedAt);
    }

    public static void WriteError(
        UIApplication application,
        string command,
        string message,
        DateTimeOffset startedAt)
    {
        if (ActionJobParser.IsAction(command))
        {
            ActionCommandExecutor.WriteError(application, command, message, startedAt);
            return;
        }
        var output = CommandResponseFileWriter.Create(
            startedAt.LocalDateTime,
            command,
            ReadCommandReader.ReadResponder(application));
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
