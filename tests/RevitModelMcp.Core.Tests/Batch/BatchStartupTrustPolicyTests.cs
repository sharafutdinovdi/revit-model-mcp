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
    public async Task Message_IdentifiesBlockingAddIn()
    {
        var message = BatchStartupTrustPolicy.Message(2027,
            "Name: QuantiMEP\nPublisher: Unknown Publisher\nLocation: C:\\Addins\\QuantiMEP.dll\nIssuer: None");
        await Assert.That(message.Contains("Revit 2027 cannot start unattended")).IsTrue();
        await Assert.That(message.Contains("'QuantiMEP' at 'C:\\Addins\\QuantiMEP.dll'")).IsTrue();
        await Assert.That(message.Contains("same Windows user")).IsTrue();
        await Assert.That(message.Contains("Load Once does not persist trust")).IsTrue();
        await Assert.That(message.Contains("unsigned Model MCP")).IsFalse();
    }

    [Test]
    public async Task ParsePrompt_RecognizesReplayBodies()
    {
        foreach (var name in new[] { "QuantiMEP", "R1 MCP", "Model MCP" })
        {
            var path = name == "Model MCP"
                ? @"C:\Users\flesh\AppData\Roaming\Autodesk\ApplicationPlugins\RevitModelMcp.bundle\.\Contents\2027\RevitModelMcp\RevitModelMcp.dll"
                : $@"C:\Addins\{name}\Plugin.dll";
            var prompt = BatchStartupTrustPolicy.ParsePrompt(
                $"Security - Unsigned Add-In\r\nName:                {name}\r\nPublisher: Unknown Publisher\r\nLocation:            {path}\r\nIssuer: None");
            await Assert.That(prompt.Name).IsEqualTo(name);
            await Assert.That(prompt.Path).IsEqualTo(path);
        }
    }

    [Test]
    public async Task ParsePrompt_CapturedQuantiMepContentText()
    {
        var body = """
            Name:                QuantiMEP
            Publisher:           Unknown Publisher
            Location:            C:\Users\flesh\AppData\Roaming\Autodesk\Revit\Addins\2027\QuantiMEP\QuantiMEP.dll
            Issuer:                 None
            Date:                   2026-10-24 15:52:52

            Make sure that this add-in comes from a trusted source.
            """;
        var prompt = BatchStartupTrustPolicy.ParsePrompt(body);
        await Assert.That(prompt.Name).IsEqualTo("QuantiMEP");
        await Assert.That(prompt.Path).IsEqualTo(@"C:\Users\flesh\AppData\Roaming\Autodesk\Revit\Addins\2027\QuantiMEP\QuantiMEP.dll");
        var message = BatchStartupTrustPolicy.Message(2027, body);
        await Assert.That(message.Contains(prompt.Name)).IsTrue();
        await Assert.That(message.Contains(prompt.Path)).IsTrue();
        await Assert.That(BatchStartupTrustPolicy.ParsePrompt(body.Replace("\n", "\r\n")))
            .IsEqualTo(prompt);
    }

    [Test]
    public async Task ParsePrompt_CapturedR1McpContentText()
    {
        var body = """
            Name:                R1 MCP
            Publisher:           Unknown Publisher
            Location:            C:\Users\flesh\AppData\Roaming\Autodesk\Revit\Addins\2027\R1Mcp\R1Mcp.dll
            Issuer:                 None
            Date:                   2026-10-28 03:18:10

            Make sure that this add-in comes from a trusted source.
            """;
        var prompt = BatchStartupTrustPolicy.ParsePrompt(body);
        await Assert.That(prompt.Name).IsEqualTo("R1 MCP");
        await Assert.That(prompt.Path).IsEqualTo(@"C:\Users\flesh\AppData\Roaming\Autodesk\Revit\Addins\2027\R1Mcp\R1Mcp.dll");
        var message = BatchStartupTrustPolicy.Message(2027, body);
        await Assert.That(message.Contains(prompt.Name)).IsTrue();
        await Assert.That(message.Contains(prompt.Path)).IsTrue();
        await Assert.That(BatchStartupTrustPolicy.ParsePrompt(body.Replace("\n", "\r\n")))
            .IsEqualTo(prompt);
    }

    [Test]
    public async Task ParsePrompt_CapturedModelMcpContentText()
    {
        var body = """
            Name:                Model MCP
            Publisher:           Unknown Publisher
            Location:            C:\Users\flesh\AppData\Roaming\Autodesk\ApplicationPlugins\RevitModelMcp.bundle\.\Contents\2027\RevitModelMcp\RevitModelMcp.dll
            Issuer:                 None
            Date:                   2026-10-29 17:13:24

            Make sure that this add-in comes from a trusted source.
            """;
        var prompt = BatchStartupTrustPolicy.ParsePrompt(body);
        await Assert.That(prompt.Name).IsEqualTo("Model MCP");
        await Assert.That(prompt.Path).IsEqualTo(@"C:\Users\flesh\AppData\Roaming\Autodesk\ApplicationPlugins\RevitModelMcp.bundle\.\Contents\2027\RevitModelMcp\RevitModelMcp.dll");
        var message = BatchStartupTrustPolicy.Message(2027, body);
        await Assert.That(message.Contains(prompt.Name)).IsTrue();
        await Assert.That(message.Contains(prompt.Path)).IsTrue();
        await Assert.That(BatchStartupTrustPolicy.ParsePrompt(body.Replace("\n", "\r\n")))
            .IsEqualTo(prompt);
    }

    [Test]
    public async Task ParsePrompt_UsesContentTextAfterEmptyNativeControls()
    {
        var prompt = BatchStartupTrustPolicy.ParsePrompt(
            "Name: \r\nLocation: \r\nName: QuantiMEP\r\nLocation: C:\\Addins\\QuantiMEP.dll\r\n");
        await Assert.That(prompt.Name).IsEqualTo("QuantiMEP");
        await Assert.That(prompt.Path).IsEqualTo(@"C:\Addins\QuantiMEP.dll");
    }

    [Test]
    public async Task ParsePrompt_MissingFieldsDoNotImplyModelMcp()
    {
        var prompt = BatchStartupTrustPolicy.ParsePrompt("Security - Unsigned Add-In");
        await Assert.That(prompt.Name).IsEqualTo("unknown add-in");
        await Assert.That(prompt.Path).IsEqualTo("location unavailable");
        var missingName = BatchStartupTrustPolicy.ParsePrompt("Name: \nLocation: C:\\Other.dll");
        await Assert.That(missingName.Name).IsEqualTo("unknown add-in");
        await Assert.That(missingName.Path).IsEqualTo(@"C:\Other.dll");
    }

    [Test]
    public async Task YearAvailability_TracksDistinctStartupFailuresPerYear()
    {
        var availability = new BatchStartupYearAvailability();
        availability.MarkStartupDeadline(2026, "Worker startup deadline expired.");
        availability.MarkTrustPrompt(2025, "Name: R1 MCP\nLocation: C:\\R1Mcp.dll");

        await Assert.That(availability.TryGetReason(2026, out var deadlineReason)).IsTrue();
        await Assert.That(deadlineReason).IsEqualTo(
            "Revit 2026 is unavailable for this batch run. Worker startup deadline expired.");
        await Assert.That(availability.TryGetReason(2025, out var trustReason)).IsTrue();
        await Assert.That(trustReason).IsEqualTo(BatchStartupTrustPolicy.Message(2025, "Name: R1 MCP\nLocation: C:\\R1Mcp.dll"));
        await Assert.That(availability.TryGetReason(2024, out _)).IsFalse();
    }
}
