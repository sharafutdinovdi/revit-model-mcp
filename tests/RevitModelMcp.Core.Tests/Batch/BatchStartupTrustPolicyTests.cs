using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchStartupTrustPolicyTests
{
    [Test]
    public async Task MatchesPrompt_RecognizesEnglishTitle()
    {
        await Assert.That(BatchStartupTrustPolicy.MatchesPrompt("security - unsigned add-in", "")).IsTrue();
    }

    [Test]
    public async Task MatchesPrompt_RecognizesDllWithLocalizedTitle()
    {
        await Assert.That(BatchStartupTrustPolicy.MatchesPrompt("Unsigned extension", "Load RevitModelMcp.DLL?")).IsTrue();
    }

    [Test]
    public async Task MatchesPrompt_RejectsUnrelatedAddIn()
    {
        await Assert.That(BatchStartupTrustPolicy.MatchesPrompt("Unsigned extension", "Load OtherAddIn.dll?")).IsFalse();
    }

    [Test]
    public async Task Decide_CoversStartupOutcomes()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var deadline = now.AddMinutes(3);
        await Assert.That(BatchStartupTrustPolicy.ShouldInspect(now, now.AddSeconds(75))).IsTrue();
        await Assert.That(BatchStartupTrustPolicy.ShouldInspect(now, now.AddSeconds(91))).IsFalse();
        await Assert.That(BatchStartupTrustPolicy.Decide(false, false, false, now.AddSeconds(75), deadline))
            .IsEqualTo(BatchStartupDecision.Continue);
        await Assert.That(BatchStartupTrustPolicy.Decide(false, false, true, now.AddSeconds(75), deadline))
            .IsEqualTo(BatchStartupDecision.TrustPrompt);
        await Assert.That(BatchStartupTrustPolicy.Decide(false, false, false, deadline, deadline))
            .IsEqualTo(BatchStartupDecision.StartupDeadline);
        await Assert.That(BatchStartupTrustPolicy.Decide(true, false, false, now, deadline))
            .IsEqualTo(BatchStartupDecision.Cancelled);
        await Assert.That(BatchStartupTrustPolicy.Decide(false, true, false, now, deadline))
            .IsEqualTo(BatchStartupDecision.WorkerExited);
        await Assert.That(BatchStartupTrustPolicy.Decide(false, false, false, now, deadline))
            .IsEqualTo(BatchStartupDecision.Continue);
    }

    [Test]
    public async Task Message_UsesExecutableYear()
    {
        await Assert.That(BatchStartupTrustPolicy.Message(2026)).IsEqualTo(
            "Revit 2026 asks to trust the unsigned Model MCP add-in. Start Revit 2026 once, choose Always Load, close Revit normally, then rerun.");
    }

    [Test]
    public async Task YearAvailability_TracksDistinctStartupFailuresPerYear()
    {
        var availability = new BatchStartupYearAvailability();
        availability.MarkStartupDeadline(2026, "Worker startup deadline expired.");
        availability.MarkTrustPrompt(2025);

        await Assert.That(availability.TryGetReason(2026, out var deadlineReason)).IsTrue();
        await Assert.That(deadlineReason).IsEqualTo(
            "Revit 2026 is unavailable for this batch run. Worker startup deadline expired.");
        await Assert.That(availability.TryGetReason(2025, out var trustReason)).IsTrue();
        await Assert.That(trustReason).IsEqualTo(BatchStartupTrustPolicy.Message(2025));
        await Assert.That(availability.TryGetReason(2024, out _)).IsFalse();
    }
}
