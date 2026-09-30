using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI.Events;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Control;

internal sealed class BatchDialogHandler
{
    private readonly string _modelPath;
    private readonly string _phase;
    private readonly IReadOnlyDictionary<(string Id, string Type), int> _choices;
    private readonly Action<BatchDialogRecord> _onUnknown;
    public List<BatchDialogRecord> Dialogs { get; } = [];
    public BatchDialogRecord? UnknownDialog { get; private set; }

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
        var message = (arguments as TaskDialogShowingEventArgs)?.Message;
        var decision = BatchDialogPolicy.Decide(dialogId, type, _choices);
        var record = new BatchDialogRecord
        {
            DialogId = dialogId,
            Type = type,
            Message = message,
            Decision = decision.Allowed ? $"allowed:{decision.OverrideResult}" : "unknown",
            Result = decision.OverrideResult,
            ModelPath = _modelPath,
            Phase = _phase,
            TimeUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        var retained = Dialogs.Count < BatchDialogPolicy.MaximumRecords;
        if (retained) Dialogs.Add(record);
        if (decision.Allowed && decision.OverrideResult.HasValue && !arguments.OverrideResult(decision.OverrideResult.Value))
        {
            record = record with { Decision = "unknown", Result = null };
            if (retained) Dialogs[^1] = record;
        }
        var redact = Environment.GetEnvironmentVariable("REVIT_MCP_REDACT_PATHS") == "1";
        var loggedPath = redact ? Path.GetFileName(_modelPath) : _modelPath;
        var loggedMessage = redact && message is not null
            ? Regex.Replace(message, @"(?:[A-Za-z]:[\\/]|\\\\)(?:[^\r\n\\/:*?""<>|]+[\\/])*[^\r\n\\/:*?""<>|]+?\.[A-Za-z0-9]{1,10}\b",
                match => Path.GetFileName(match.Value))
            : message;
        PluginLog.Info($"Batch dialog. DialogId='{SingleLine(dialogId)}' Type='{SingleLine(type)}' Message='{SingleLine(loggedMessage)}' Decision='{record.Decision}' Result='{record.Result}' ModelPath='{SingleLine(loggedPath)}' Phase='{record.Phase}' TimeUtc='{record.TimeUtc}'.");
        if (record.Decision == "unknown" && UnknownDialog is null)
        {
            UnknownDialog = record;
            _onUnknown(record);
        }
    }

    private static string SingleLine(string? value) => value?.Replace('\r', ' ').Replace('\n', ' ') ?? "null";
}
