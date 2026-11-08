using System.Text.Json;
using Autodesk.Revit.UI;
using Nice3point.TUnit.Revit;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Batch;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Addin.Tests;

public sealed class SaveReminderTests : RevitApiUiTest
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SaveReminder_CancelsQueuedOrRunningJobAndReportsWithoutSaving(bool running)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rmm-dialog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var channel = new ControlChannel(directory);
        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var payload = $$"""{"command":"ping","jobId":"{{jobId}}","clientId":"dialog-test","clientName":"Dialog test"}""";
            channel.SubmitHttp(ControlJobParser.Parse(payload), payload, out _);
            if (running) channel.Scheduler.TakeNext();
            var selectedResult = 0;
            var dismissed = channel.DismissSaveReminder("TaskDialog_Project_Not_Saved_Recently",
                "TaskDialogShowingEventArgs", result =>
                {
                    selectedResult = result;
                    return true;
                });
            await Assert.That(dismissed).IsTrue();
            await Assert.That(selectedResult).IsEqualTo((int)TaskDialogResult.Cancel);
            if (running) return;
            channel.Tick(UiApplication);
            using var response = JsonDocument.Parse(channel.Scheduler.Status(jobId)!.Result!);
            await Assert.That(response.RootElement.GetProperty("dialogsSuppressed")[0].GetString())
                .IsEqualTo(BatchDialogPolicy.SaveReminderDismissed);
        }
        finally
        {
            channel.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task SaveReminder_WithoutPendingJobDoesNotAnswer()
    {
        var channel = new ControlChannel(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var answered = false;
        var dismissed = channel.DismissSaveReminder("TaskDialog_Project_Not_Saved_Recently",
            "TaskDialogShowingEventArgs", _ => answered = true);
        await Assert.That(dismissed).IsFalse();
        await Assert.That(answered).IsFalse();
        channel.Shutdown();
    }
}
