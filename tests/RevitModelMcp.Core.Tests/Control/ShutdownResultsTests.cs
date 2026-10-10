using System.Text.Json;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ShutdownResultsTests
{
    [Test]
    public async Task QueuedJobsAreCancelledWithTheClosingReason()
    {
        var scheduler = new JobScheduler();
        scheduler.Submit("a-1", "a", "a", "ping", "{}");
        var changed = scheduler.AbandonUnfinished(ShutdownResults.Running, ShutdownResults.Queued);
        var job = changed.Single();
        await Assert.That(job.State).IsEqualTo(JobState.Cancelled);
        using var document = JsonDocument.Parse(job.Result!);
        await Assert.That(document.RootElement.GetProperty("cancelled").GetBoolean()).IsTrue();
        await Assert.That(document.RootElement.GetProperty("error").GetString()).Contains("Revit is closing");
    }

    [Test]
    [Arguments(ShutdownResults.Queued)]
    [Arguments(ShutdownResults.Running)]
    [Arguments(ShutdownResults.Unknown)]
    public async Task ResultsAreValidJsonFailures(string result)
    {
        using var document = JsonDocument.Parse(result);
        await Assert.That(document.RootElement.GetProperty("success").GetBoolean()).IsFalse();
    }
}
