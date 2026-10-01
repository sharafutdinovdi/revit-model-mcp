using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI.Events;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Control;

internal sealed class BatchDialogHandler
{
    private readonly object _sync = new();
    private readonly string _modelPath;
    private readonly string _phase;
    private readonly IReadOnlyDictionary<(string Id, string Type), int> _choices;
    private readonly Action<BatchDialogRecord> _onUnknown;
    private readonly List<BatchDialogRecord> _dialogs = [];
    private BatchDialogRecord? _unknownDialog;
    private Task? _pendingCapture;

    public List<BatchDialogRecord> Dialogs { get { lock (_sync) return _dialogs.ToList(); } }
    public BatchDialogRecord? UnknownDialog { get { lock (_sync) return _unknownDialog; } }

    public BatchDialogHandler(string modelPath, string phase, IReadOnlyDictionary<(string Id, string Type), int> choices,
        Action<BatchDialogRecord> onUnknown)
    {
        _modelPath = modelPath;
        _phase = phase;
        _choices = choices;
        _onUnknown = onUnknown;
    }

    public void OnDialog(object? sender, DialogBoxShowingEventArgs arguments)
    {
        var dialogId = arguments.DialogId ?? string.Empty;
        var type = arguments.GetType().Name;
        var message = arguments switch
        {
            TaskDialogShowingEventArgs task => BatchDialogText.Cap(task.Message),
            MessageBoxShowingEventArgs box => BatchDialogText.Cap(box.Message),
            _ => null
        };
        var buttons = arguments is MessageBoxShowingEventArgs messageBox
            ? StandardButtons(messageBox.DialogType)
            : null;
        var decision = BatchDialogPolicy.Decide(dialogId, type, _choices);
        var record = new BatchDialogRecord
        {
            DialogId = dialogId,
            Type = type,
            Message = message,
            Buttons = buttons,
            Decision = decision.Allowed ? $"allowed:{decision.OverrideResult}" : "unknown",
            Result = decision.OverrideResult,
            ModelPath = _modelPath,
            Phase = _phase,
            TimeUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        var recordIndex = -1;
        lock (_sync)
        {
            if (_dialogs.Count < BatchDialogPolicy.MaximumRecords)
            {
                recordIndex = _dialogs.Count;
                _dialogs.Add(record);
            }
        }
        if (decision.Allowed && decision.OverrideResult.HasValue && !arguments.OverrideResult(decision.OverrideResult.Value))
        {
            record = record with { Decision = "unknown", Result = null };
            ReplaceRecord(recordIndex, record);
        }
        if (record.Decision != "unknown")
        {
            Log(record);
            return;
        }
        if (message is null && arguments is not TaskDialogShowingEventArgs)
        {
            var threadId = NativeDialogReader.CurrentThreadId;
            var existing = NativeDialogReader.VisibleWindows(threadId);
            var initialRecord = record;
            lock (_sync)
                _pendingCapture = Task.Run(() => CaptureAndPublish(initialRecord, threadId, existing, recordIndex));
            return;
        }
        CompleteUnknown(record);
    }

    public void WaitForPendingCapture()
    {
        Task? pending;
        lock (_sync) pending = _pendingCapture;
        pending?.Wait();
    }

    private void CaptureAndPublish(BatchDialogRecord record, uint threadId, HashSet<IntPtr> existing, int recordIndex)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var window = NativeDialogReader.FindNewWindow(threadId, existing);
                if (window != IntPtr.Zero)
                {
                    var (message, buttons) = BatchDialogText.Parse(NativeDialogReader.Read(window));
                    record = record with
                    {
                        Message = message ?? record.Message,
                        Buttons = buttons.Count > 0 ? buttons.ToList() : record.Buttons
                    };
                    break;
                }
                Thread.Sleep(100);
            }
        }
        catch (Exception exception)
        {
            PluginLog.Info($"Batch dialog capture failed: {exception.GetType().Name}.");
        }
        ReplaceRecord(recordIndex, record);
        CompleteUnknown(record);
    }

    private void ReplaceRecord(int index, BatchDialogRecord record)
    {
        lock (_sync)
        {
            if (index >= 0) _dialogs[index] = record;
        }
    }

    private void CompleteUnknown(BatchDialogRecord record)
    {
        Log(record);
        lock (_sync)
        {
            if (_unknownDialog is not null) return;
            _unknownDialog = record;
        }
        _onUnknown(record);
    }

    private void Log(BatchDialogRecord record)
    {
        var redact = Environment.GetEnvironmentVariable("REVIT_MCP_REDACT_PATHS") == "1";
        var loggedPath = redact ? Path.GetFileName(_modelPath) : _modelPath;
        var buttonDetails = record.Buttons is { Count: > 0 } buttons
            ? string.Join(", ", buttons.Select(button => $"{button.Result}={button.Caption}"))
            : "none";
        var description = record.Decision == "unknown"
            ? BatchDialogText.DescribeUnknown(record)
            : $"{record.Message ?? "No message available."} Buttons: {buttonDetails}.";
        var loggedDescription = redact
            ? Regex.Replace(description, @"(?:[A-Za-z]:[\\/]|\\\\)(?:[^\r\n\\/:*?""<>|]+[\\/])*[^\r\n\\/:*?""<>|]+?\.[A-Za-z0-9]{1,10}\b",
                match => Path.GetFileName(match.Value))
            : description;
        PluginLog.Info($"Batch dialog. DialogId='{SingleLine(record.DialogId)}' Type='{SingleLine(record.Type)}' Details='{SingleLine(loggedDescription)}' Decision='{record.Decision}' Result='{record.Result}' ModelPath='{SingleLine(loggedPath)}' Phase='{record.Phase}' TimeUtc='{record.TimeUtc}'.");
    }

    private static List<BatchDialogButton>? StandardButtons(int dialogType)
    {
        var buttons = (dialogType & 0xF) switch
        {
            0 => new[] { ("OK", 1) },
            1 => new[] { ("OK", 1), ("Cancel", 2) },
            2 => new[] { ("Abort", 3), ("Retry", 4), ("Ignore", 5) },
            3 => new[] { ("Yes", 6), ("No", 7), ("Cancel", 2) },
            4 => new[] { ("Yes", 6), ("No", 7) },
            5 => new[] { ("Retry", 4), ("Cancel", 2) },
            6 => new[] { ("Cancel", 2), ("Try Again", 10), ("Continue", 11) },
            _ => []
        };
        return buttons.Length == 0
            ? null
            : buttons.Select(button => new BatchDialogButton { Caption = button.Item1, Result = button.Item2 }).ToList();
    }

    private static string SingleLine(string? value) => value?.Replace('\r', ' ').Replace('\n', ' ') ?? "null";
}
