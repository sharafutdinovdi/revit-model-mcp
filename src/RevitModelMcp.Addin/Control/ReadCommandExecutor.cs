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
            "Команда принята и выполняется.",
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
                           ?? throw new InvalidOperationException("Нет активного документа Revit.");
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
                            $"Достигнут лимит 60 секунд. Обработано видов: {views.Processed} из {views.Total}.",
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
                            "Элементы видов не читались; их состав доступен через view-summary.");
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
                        "Сопоставление выполнено по участвующим элементам предупреждений.");
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
                    WriteFailure<object>(output, job.Command, "Команда не относится к быстрым командам чтения.", stopwatch);
                    break;
            }
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            PluginLog.Error($"Job processing failed. Command='{job.Command}'.", exception);
            output.Write(CommandResponse<object>.Fail(
                job.Command,
                $"Не удалось выполнить команду: {exception}",
                stopwatch.ElapsedMilliseconds));
            LogFinished(job.Command, "error", stopwatch.ElapsedMilliseconds, output.FilePath, exception.Message);
        }
    }

    public static void WriteInvalid(
        UIApplication application,
        ControlJobParseResult job,
        DateTimeOffset startedAt)
    {
        WriteError(application, job.Command, job.Error ?? "Некорректное задание.", startedAt);
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
        var current = string.IsNullOrWhiteSpace(currentView) ? string.Empty : $" Текущий вид: «{currentView}».";
        output.Write(CommandResponse<ViewListData>.PartialResult(
            command,
            data,
            $"Обработано видов: {data.Processed} из {data.Total}.{current} Элементы видов не читались.",
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
                $"Не удалось экспортировать вид «{view.Name}» типа {view.ViewType}: {exception.Message}",
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
                ? "Достигнут лимит 60 секунд; результат помечен частичным."
                : $"{message} Достигнут лимит 60 секунд; результат помечен частичным.";
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
                ? "Команда заняла больше двух секунд; стоимость указана в elapsedMs."
                : $"{message} Команда заняла больше двух секунд; стоимость указана в elapsedMs.";
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
