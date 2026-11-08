using System.Runtime.Serialization;
using Autodesk.Revit.UI.Events;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Control;

internal sealed class JobDialogScope : IDisposable
{
    [ThreadStatic] internal static JobDialogScope? Current;
    private readonly JobDialogScope? _previous;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _monitor;
    private bool _dismissed;
    private readonly Action<BatchDialogRecord> _publish;

    internal JobDialogScope(bool dismissed, Action<BatchDialogRecord> publish)
    {
        _dismissed = dismissed;
        _publish = publish;
        _previous = Current;
        Current = this;
        var threadId = NativeDialogReader.CurrentThreadId;
        _monitor = Task.Run(async () =>
        {
            var seen = new HashSet<IntPtr>();
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    BatchDialogGuard.Run(() =>
                    {
                        foreach (var window in NativeDialogReader.ModalWindows(threadId))
                        {
                            if (!seen.Add(window)) continue;
                            var title = NativeDialogReader.Title(window);
                            var (message, buttons) = BatchDialogText.Parse(NativeDialogReader.Read(window));
                            publish(new BatchDialogRecord
                            {
                                DialogId = title,
                                Type = "NativeModalDialog",
                                Message = message ?? title,
                                Buttons = buttons.ToList(),
                                Decision = "unknown",
                                ModelPath = string.Empty,
                                Phase = "execute",
                                TimeUtc = DateTimeOffset.UtcNow.ToString("O")
                            });
                        }
                    }, exception => PluginLog.Info($"Job dialog inspection failed: {exception.GetType().Name}."));
                    await Task.Delay(100, _cancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    internal void RecordDismissal() => _dismissed = true;
    internal string? DismissalWarning => _dismissed ? BatchDialogPolicy.SaveReminderDismissed : null;

    internal void ReportDialog(DialogBoxShowingEventArgs arguments)
    {
        _publish(new BatchDialogRecord
        {
            DialogId = arguments.DialogId ?? string.Empty,
            Type = arguments.GetType().Name,
            Message = BatchDialogText.Cap(arguments switch
            {
                TaskDialogShowingEventArgs task => task.Message,
                MessageBoxShowingEventArgs box => box.Message,
                _ => null
            }),
            Decision = "unknown",
            ModelPath = string.Empty,
            Phase = "execute",
            TimeUtc = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    internal void Finish()
    {
        _cancellation.Cancel();
        _monitor.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Finish();
        _cancellation.Dispose();
        Current = _previous;
    }
}

[DataContract]
internal sealed class JobDialogProgress
{
    [DataMember(Name = "state")] public string State { get; init; } = "waiting_dialog";
    [DataMember(Name = "dialog")] public required BatchDialogRecord Dialog { get; init; }
}
