using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal sealed class ViewElementsSession : IControlSession
{
    private const int ElementsPerExecution = 100;
    private const int MaximumExecutions = 1_200;
    private const long MaximumDurationMs = 120_000;

    private readonly Document _document;
    private readonly IReadOnlyList<ElementId> _pageIds;
    private readonly ViewElementReader _reader;
    private readonly ViewElementsData _data;
    private readonly CommandResponseFileWriter _output;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<string> _unknownCategories;

    private int _nextElementIndex;
    private int _executions;

    public ViewElementsSession(
        UIApplication application,
        ControlJobParseResult job,
        DateTimeOffset startedAt)
    {
        _document = application.ActiveUIDocument?.Document
                    ?? throw new InvalidOperationException("Нет активного документа Revit.");
        _output = CommandResponseFileWriter.Create(
            startedAt.LocalDateTime,
            job.Command,
            ReadCommandReader.ReadResponder(application));
        _data = new ViewElementsData
        {
            View = job.View!,
            Categories = job.Categories.ToList(),
            Offset = job.Offset,
            Limit = job.Limit
        };
        WritePartial("Команда принята; подготавливается список элементов вида.");

        var view = ReadCommandReader.FindView(_document, job.View!);
        if (view is null)
        {
            Fail($"Вид «{job.View}» не найден.");
            _pageIds = Array.Empty<ElementId>();
            _reader = new ViewElementReader(_document, new HashSet<long>());
            _unknownCategories = new List<string>();
            return;
        }

        var categoryIds = ResolveCategoryIds(_document, job.Categories, out _unknownCategories);
        var collector = new FilteredElementCollector(_document, view.Id)
            .WhereElementIsNotElementType();
        if (job.Categories.Count > 0)
        {
            if (categoryIds.Count == 0)
            {
                _pageIds = Array.Empty<ElementId>();
                _data.Total = 0;
                _data.HasMore = false;
                _reader = new ViewElementReader(_document, ReadCommandReader.ReadWarningElementIds(_document));
                return;
            }

            collector.WherePasses(new ElementMulticategoryFilter(categoryIds));
        }

        var allIds = collector.ToElementIds()
            .OrderBy(RevitValueReader.GetId)
            .ToList();
        var page = PageSlice.Create(allIds, job.Offset, job.Limit);
        _pageIds = page.Items;
        _data.Total = allIds.Count;
        _data.HasMore = page.HasMore;
        _reader = new ViewElementReader(_document, ReadCommandReader.ReadWarningElementIds(_document));
        WritePartial("Список элементов подготовлен; чтение данных продолжается порциями.");
    }

    public bool IsFinished { get; private set; }

    public void ProcessTick(UIApplication application)
    {
        if (IsFinished)
        {
            return;
        }

        try
        {
            _executions++;
            if (_executions > MaximumExecutions || _stopwatch.ElapsedMilliseconds >= MaximumDurationMs)
            {
                StopWithPartial(
                    $"Достигнут предел обработки: {_executions} вызовов ExternalEvent или 120 секунд. " +
                    $"Обработано элементов: {_data.Processed} из {_pageIds.Count}.");
                return;
            }

            if (application.ActiveUIDocument?.Document != _document)
            {
                Fail("Активный документ изменился во время чтения элементов.");
                return;
            }

            var processed = 0;
            while (_nextElementIndex < _pageIds.Count && processed < ElementsPerExecution)
            {
                var element = _document.GetElement(_pageIds[_nextElementIndex++]);
                processed++;
                _data.Processed = _nextElementIndex;
                if (element is not null)
                {
                    _data.Elements.Add(_reader.Read(element));
                }
            }

            if (_nextElementIndex >= _pageIds.Count)
            {
                Complete();
            }
            else
            {
                WritePartial("Обработка ожидает следующий вызов ExternalEvent.");
            }
        }
        catch (Exception exception)
        {
            PluginLog.Error("View-elements processing failed.", exception);
            StopWithPartial($"Чтение элементов остановлено: {exception}");
        }
    }

    public void RejectJobWhileBusy()
    {
        _data.RejectedJobsWhileBusy++;
        WritePartial("Новое задание отклонено: RevitModelMcp занят чтением элементов.");
    }

    public void Abort(Exception exception)
    {
        PluginLog.Error("View-elements session aborted.", exception);
        StopWithPartial($"Чтение элементов аварийно остановлено: {exception}");
    }

    private void Complete()
    {
        _stopwatch.Stop();
        var messages = new List<string>();
        if (_unknownCategories.Count > 0)
        {
            messages.Add($"Категории не найдены: {string.Join(", ", _unknownCategories)}.");
        }

        if (_data.RejectedJobsWhileBusy > 0)
        {
            messages.Add($"Отклонено заданий во время чтения: {_data.RejectedJobsWhileBusy}.");
        }

        _output.Write(CommandResponse<ViewElementsData>.Ok(
            "view-elements",
            _data,
            _stopwatch.ElapsedMilliseconds,
            messages.Count == 0 ? null : string.Join(" ", messages)));
        _output.Log(
            "view-elements",
            "completed",
            _data.Processed,
            _data.Total,
            _stopwatch.ElapsedMilliseconds,
            _data.View);
        LogFinished("success", null);
        IsFinished = true;
    }

    private void Fail(string message)
    {
        _stopwatch.Stop();
        _output.Write(CommandResponse<ViewElementsData>.Fail(
            "view-elements",
            message,
            _stopwatch.ElapsedMilliseconds));
        _output.Log(
            "view-elements",
            "error",
            _data.Processed,
            _data.Total,
            _stopwatch.ElapsedMilliseconds,
            _data.View);
        LogFinished("error", message);
        IsFinished = true;
    }

    private void StopWithPartial(string message)
    {
        _stopwatch.Stop();
        _output.Write(CommandResponse<ViewElementsData>.PartialResult(
            "view-elements",
            _data,
            message,
            _stopwatch.ElapsedMilliseconds));
        _output.Log(
            "view-elements",
            "partial",
            _data.Processed,
            _data.Total,
            _stopwatch.ElapsedMilliseconds,
            _data.View);
        LogFinished("partial", message);
        IsFinished = true;
    }

    private void WritePartial(string message)
    {
        _output.Write(CommandResponse<ViewElementsData>.PartialResult(
            "view-elements",
            _data,
            message,
            _stopwatch.ElapsedMilliseconds));
        _output.Log(
            "view-elements",
            "processing",
            _data.Processed,
            _data.Total,
            _stopwatch.ElapsedMilliseconds,
            _data.View);
    }

    private void LogFinished(string outcome, string? message)
    {
        PluginLog.Info(
            $"Job processing finished. Command='view-elements'. Outcome='{outcome}'. " +
            $"Processed={_data.Processed}. Total={_data.Total}. ElapsedMs={_stopwatch.ElapsedMilliseconds}. " +
            $"ResponsePath='{_output.FilePath}'. Message='{message ?? string.Empty}'.");
    }

    private static List<ElementId> ResolveCategoryIds(
        Document document,
        IReadOnlyList<string> requestedNames,
        out List<string> unknownNames)
    {
        var requested = requestedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<ElementId>();
        foreach (Category category in document.Settings.Categories)
        {
            if (!requested.Contains(category.Name))
            {
                continue;
            }

            ids.Add(category.Id);
            matchedNames.Add(category.Name);
        }

        unknownNames = requestedNames.Where(name => !matchedNames.Contains(name)).ToList();
        return ids;
    }
}
