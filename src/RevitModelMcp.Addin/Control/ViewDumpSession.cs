using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal sealed class ViewDumpSession : IControlSession
{
    // Batches of 100 elements require 40–100 short calls for a typical view with 4,000–10,000 elements.
    private const int ElementsPerExecution = 100;
    private const int ProgressWriteIntervalElements = 500;
    private const int MaximumExecutions = 1_200;
    private const long MaximumDurationMs = 120_000;

    private readonly UIDocument _uiDocument;
    private readonly Document _document;
    private readonly ElementId _originalViewId;
    private readonly HashSet<long> _initialOpenViewIds;
    private readonly Dictionary<long, string> _openedBySession = new();
    private readonly HashSet<long> _warningElementIds;
    private readonly IReadOnlyList<string> _requestedViews;
    private readonly ViewDumpOutput _output;
    private readonly ViewDumpReport _report;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private int _nextViewIndex;
    private ViewDumpView? _currentResult;
    private IReadOnlyList<ElementId> _currentElementIds = Array.Empty<ElementId>();
    private int _currentElementIndex;
    private int _executions;
    private Dictionary<string, CategoryAccumulator> _categories = new(StringComparer.Ordinal);

    public ViewDumpSession(UIApplication application, IReadOnlyList<string> requestedViews, DateTimeOffset startedAt)
    {
        _uiDocument = application.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        _document = _uiDocument.Document;
        var originalView = _document.ActiveView ?? throw new InvalidOperationException("No active Revit view.");
        _originalViewId = originalView.Id;
        _requestedViews = requestedViews;
        _initialOpenViewIds = _uiDocument.GetOpenUIViews()
            .Select(view => RevitValueReader.GetId(view.ViewId))
            .ToHashSet();
        _warningElementIds = ReadCommandReader.ReadWarningElementIds(_document);
        _output = ViewDumpOutput.Create(startedAt.LocalDateTime);
        _report = new ViewDumpReport
        {
            StartedAt = FormatTime(startedAt),
            UpdatedAt = FormatTime(startedAt),
            DocumentTitle = _document.Title,
            Responder = ReadCommandReader.ReadResponder(application),
            OriginalViewName = originalView.Name,
            Message = "Job is running."
        };
        WriteReport();
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
                Fail(
                    $"Processing limit reached: {_executions} ExternalEvent calls or 120 seconds. " +
                    $"Processed {_report.ProcessedElements} of {_report.TotalElements} elements.");
                return;
            }

            if (application.ActiveUIDocument?.Document != _document)
            {
                Fail("The active document changed during the dump.");
                return;
            }

            if (_currentResult is null && !StartNextView())
            {
                Complete();
                return;
            }

            ProcessCurrentBatch();
        }
        catch (Exception exception)
        {
            PluginLog.Error("Views-dump processing failed.", exception);
            Fail($"Dump stopped: {exception}");
        }
    }

    public void RejectJobWhileBusy()
    {
        _report.RejectedJobsWhileBusy++;
        _report.Message = "New job rejected: RevitModelMcp is busy with the current dump.";
        WriteReport();
    }

    public void Abort(Exception exception)
    {
        PluginLog.Error("Views-dump session aborted.", exception);
        Fail($"Dump aborted: {exception}");
    }

    private bool StartNextView()
    {
        while (_nextViewIndex < _requestedViews.Count)
        {
            var requestedName = _requestedViews[_nextViewIndex++];
            var view = FindView(requestedName);
            if (view is null)
            {
                _report.Views.Add(ViewDumpView.Missing(requestedName));
                WriteReport();
                continue;
            }

            try
            {
                _uiDocument.ActiveView = view;
                TrackOpenedView(view);
                var ids = new FilteredElementCollector(_document, view.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .OrderBy(RevitValueReader.GetId)
                    .ToList();
                _currentElementIds = ids;
                _currentElementIndex = 0;
                _categories = new Dictionary<string, CategoryAccumulator>(StringComparer.Ordinal);
                _currentResult = new ViewDumpView
                {
                    RequestedName = requestedName,
                    Status = "running",
                    Header = ReadHeader(view, ids.Count)
                };
                _report.TotalElements += ids.Count;
                _report.Views.Add(_currentResult);
                WriteReport();
                return true;
            }
            catch (Exception exception)
            {
                PluginLog.Error($"Views-dump view failed. View='{requestedName}'.", exception);
                _report.Views.Add(new ViewDumpView
                {
                    RequestedName = requestedName,
                    Status = "error",
                    Error = $"Failed to open or read the view: {exception.Message}"
                });
                WriteReport();
            }
        }

        return false;
    }

    private void ProcessCurrentBatch()
    {
        var reader = new ViewElementReader(_document, _warningElementIds);
        var processedThisExecution = 0;
        while (_currentElementIndex < _currentElementIds.Count && processedThisExecution < ElementsPerExecution)
        {
            var elementId = _currentElementIds[_currentElementIndex++];
            processedThisExecution++;
            _report.ProcessedElements++;
            var element = _document.GetElement(elementId);
            if (element is null)
            {
                continue;
            }

            var dump = reader.Read(element);
            _currentResult!.Elements.Add(dump);
            AccumulateCategory(element, dump, reader);
        }

        if (_currentElementIndex >= _currentElementIds.Count)
        {
            FinishCurrentView();
            WriteReport();
        }
        else if (_report.ProcessedElements % ProgressWriteIntervalElements == 0)
        {
            WriteReport();
        }
    }

    private void FinishCurrentView()
    {
        _currentResult!.Categories = _categories.Values
            .OrderByDescending(category => category.Count)
            .ThenBy(category => category.Name, StringComparer.Ordinal)
            .Select(category => new ViewCategorySummary
            {
                Category = category.Name,
                Count = category.Count,
                DifferentTypes = category.TypeIds.Count
            })
            .ToList();
        _currentResult.Status = "completed";
        _currentResult = null;
        _currentElementIds = Array.Empty<ElementId>();
        _currentElementIndex = 0;
    }

    private void Complete()
    {
        _stopwatch.Stop();
        RestoreOriginalViewAndCloseOwnedViews();
        _report.Status = "completed";
        _report.Message = _report.Views.Any(view => view.Status is "error" or "not-found")
            ? "Dump completed with issues in some views."
            : "Dump completed.";
        var now = DateTimeOffset.Now;
        _report.CompletedAt = FormatTime(now);
        _report.UpdatedAt = FormatTime(now);
        WriteReport(false);
        PluginLog.Info(
            $"Job processing finished. Command='views-dump'. Outcome='{(_report.Views.Any(view => view.Status is "error" or "not-found") ? "partial" : "success")}'. " +
            $"Processed={_report.ProcessedElements}. Total={_report.TotalElements}. ElapsedMs={_stopwatch.ElapsedMilliseconds}. " +
            $"ResponsePath='{_output.JsonPath}'.");
        IsFinished = true;
    }

    private void Fail(string message)
    {
        _stopwatch.Stop();
        RestoreOriginalViewAndCloseOwnedViews();
        _report.Status = _report.ProcessedElements > 0 ? "partial" : "error";
        _report.Message = message;
        var now = DateTimeOffset.Now;
        _report.CompletedAt = FormatTime(now);
        _report.UpdatedAt = FormatTime(now);
        WriteReport(false);
        PluginLog.Info(
            $"Job processing finished. Command='views-dump'. Outcome='{_report.Status}'. " +
            $"Processed={_report.ProcessedElements}. Total={_report.TotalElements}. ElapsedMs={_stopwatch.ElapsedMilliseconds}. " +
            $"ResponsePath='{_output.JsonPath}'. Message='{message}'.");
        IsFinished = true;
    }

    private void RestoreOriginalViewAndCloseOwnedViews()
    {
        try
        {
            var original = _document.GetElement(_originalViewId) as View;
            if (original is not null)
            {
                _uiDocument.ActiveView = original;
                _report.OriginalViewRestored = true;
            }
            else
            {
                _report.OriginalViewRestored = false;
            }
        }
        catch (Exception exception)
        {
            PluginLog.Error("Views-dump could not restore the original view.", exception);
            _report.OriginalViewRestored = false;
        }

        IList<UIView> openViews;
        try
        {
            openViews = _uiDocument.GetOpenUIViews();
        }
        catch (Exception exception)
        {
            PluginLog.Error("Views-dump could not read open UI views.", exception);
            return;
        }

        foreach (var uiView in openViews.ToList())
        {
            var id = RevitValueReader.GetId(uiView.ViewId);
            if (!_openedBySession.TryGetValue(id, out var name) || uiView.ViewId == _document.ActiveView?.Id)
            {
                continue;
            }

            try
            {
                uiView.Close();
                _report.ClosedViews.Add(name);
            }
            catch (Exception exception)
            {
                // Failure to close a view opened by the dump must not prevent saving the report.
                PluginLog.Error($"Views-dump could not close an owned view. View='{name}'.", exception);
            }
        }
    }

    private void TrackOpenedView(View view)
    {
        var id = RevitValueReader.GetId(view.Id);
        if (_initialOpenViewIds.Contains(id) || _openedBySession.ContainsKey(id))
        {
            return;
        }

        _openedBySession[id] = view.Name;
        _report.OpenedViews.Add(view.Name);
    }

    private View? FindView(string name)
    {
        return new FilteredElementCollector(_document)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => !view.IsTemplate && string.Equals(view.Name, name, StringComparison.Ordinal));
    }

    private ViewDumpHeader ReadHeader(View view, int elementCount)
    {
        var filterIds = view.GetFilters();
        return new ViewDumpHeader
        {
            Name = view.Name,
            Type = view.ViewType.ToString(),
            Level = (view as ViewPlan)?.GenLevel?.Name,
            Scale = view.Scale,
            Template = RevitValueReader.IsValidId(view.ViewTemplateId)
                ? _document.GetElement(view.ViewTemplateId)?.Name
                : null,
            Discipline = RevitValueReader.GetParameterText(view.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE)),
            FilterCount = filterIds.Count,
            GraphicOverrideCount = filterIds.Count(filterId => HasGraphicOverrides(view.GetFilterOverrides(filterId))),
            ElementCount = elementCount
        };
    }

    private static bool HasGraphicOverrides(OverrideGraphicSettings settings)
    {
        return settings.Halftone ||
               settings.Transparency > 0 ||
               settings.ProjectionLineWeight >= 0 ||
               settings.CutLineWeight >= 0 ||
               settings.ProjectionLineColor.IsValid ||
               settings.CutLineColor.IsValid ||
               RevitValueReader.IsValidId(settings.ProjectionLinePatternId) ||
               RevitValueReader.IsValidId(settings.CutLinePatternId) ||
               RevitValueReader.IsValidId(settings.SurfaceForegroundPatternId) ||
               RevitValueReader.IsValidId(settings.CutForegroundPatternId);
    }

    private void AccumulateCategory(Element element, ViewElementDump dump, ViewElementReader reader)
    {
        var name = dump.Category ?? "<no category>";
        if (!_categories.TryGetValue(name, out var category))
        {
            category = new CategoryAccumulator(name);
            _categories[name] = category;
        }

        category.Count++;
        var typeIdentity = reader.GetTypeIdentity(element, dump);
        if (!string.IsNullOrWhiteSpace(typeIdentity))
        {
            category.TypeIds.Add(typeIdentity!);
        }
    }

    private void WriteReport(bool updateTime = true)
    {
        if (updateTime)
        {
            _report.UpdatedAt = FormatTime(DateTimeOffset.Now);
        }

        _output.Write(_report);
        PluginLog.Info(
            $"Progress. Command='views-dump'. State='{_report.Status}'. Processed={_report.ProcessedElements}. " +
            $"Total={_report.TotalElements}. ElapsedMs={_stopwatch.ElapsedMilliseconds}. " +
            $"CurrentView='{_currentResult?.RequestedName ?? string.Empty}'. ResponsePath='{_output.JsonPath}'.");
    }

    private static string FormatTime(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }
    private sealed class CategoryAccumulator
    {
        public CategoryAccumulator(string name) => Name = name;
        public string Name { get; }
        public int Count { get; set; }
        public HashSet<string> TypeIds { get; } = new(StringComparer.Ordinal);
    }
}
