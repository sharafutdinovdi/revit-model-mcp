using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Activity;

/// <summary>
/// Shared access point the WPF activity pane uses to reach the running add-in: the job scheduler for the
/// queue section, a job-cancel callback, and a dispatcher that raises one-shot Revit API work (selection,
/// undo) through a Toolkit external event, never calling the API from WPF directly.
/// </summary>
internal static class ActivityHost
{
    public static JobScheduler? Scheduler { get; set; }
    public static Func<string, string, JobCancellation>? CancelJob { get; set; }

    public static void Dispatch(Action<UIApplication> action) =>
        new Nice3point.Revit.Toolkit.External.ExternalEvent(action).Raise();

    public static void Reset()
    {
        Scheduler = null;
        CancelJob = null;
    }
}
