using System.Text.Json;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Models;

public sealed class SkippedReadDiagnosticsTests
{
    [Test]
    public async Task Snapshot_MergesNestedDiagnosticsWithoutCountingThemTwice()
    {
        var diagnostics = new SkippedReadDiagnostics();
        for (var index = 0; index < 125; index++)
            diagnostics.Add($"snapshot scope {index}", new InvalidOperationException("Unavailable."));
        var snapshot = new ModelSnapshotData { Skipped = diagnostics.Items, SkippedCount = diagnostics.Count };
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            var response = CommandResponse<ModelSnapshotData>.Ok("model-snapshot", snapshot, 1);
            for (var index = 0; index < 2; index++)
            {
                using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
                var root = json.RootElement;
                await Assert.That(root.GetProperty("skippedCount").GetInt32()).IsEqualTo(125);
                await Assert.That(root.GetProperty("skipped").GetArrayLength()).IsEqualTo(100);
                await Assert.That(root.GetProperty("partial").GetBoolean()).IsTrue();
                await Assert.That(root.GetProperty("data").GetProperty("skippedCount").GetInt32()).IsEqualTo(125);
            }
        }
        finally { SkippedReadDiagnostics.Current = null; }
    }

    [Test]
    public async Task FamilyAudit_MergesUnsupportedFailedAndNestedPortions()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("family Broken audit", new InvalidOperationException("Cannot inspect."));
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            var data = new FamilyAuditData
            {
                Skipped = [new SkippedRead { What = "other family", Reason = "Dialog suppressed." }],
                SkippedCount = 1,
                Families = [
                    new FamilyAuditFamily { Name = "In place", Status = "skipped", Reason = "Unsupported." },
                    new FamilyAuditFamily { Name = "Broken", Status = "failed", Reason = "Cannot inspect." }
                ]
            };
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<FamilyAuditData>.Ok("family-audit", data, 1)));
            await Assert.That(json.RootElement.GetProperty("skippedCount").GetInt32()).IsEqualTo(3);
            await Assert.That(json.RootElement.GetProperty("partial").GetBoolean()).IsTrue();
            await Assert.That(json.RootElement.GetProperty("skipped")[2].GetProperty("reason").GetString())
                .IsEqualTo("Unsupported.");
        }
        finally { SkippedReadDiagnostics.Current = null; }
    }

    [Test]
    public async Task FamilyAudit_CappedEnvelopeRemainsStableWhenSerializedAgain()
    {
        var data = new FamilyAuditData
        {
            Families = Enumerable.Range(0, 125).Select(index => new FamilyAuditFamily
            {
                Name = $"Family {index}", Status = "skipped", Reason = "Unsupported."
            }).ToList()
        };
        var response = CommandResponse<FamilyAuditData>.Ok("family-audit", data, 1);
        for (var index = 0; index < 2; index++)
        {
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(response));
            await Assert.That(json.RootElement.GetProperty("skippedCount").GetInt32()).IsEqualTo(125);
            await Assert.That(json.RootElement.GetProperty("skipped").GetArrayLength()).IsEqualTo(100);
        }
    }

    [Test]
    public async Task FamilyAudit_FailuresBeyondEnvelopeSampleAreNotCountedTwice()
    {
        var diagnostics = new SkippedReadDiagnostics();
        var data = new FamilyAuditData();
        for (var index = 0; index < 125; index++)
        {
            diagnostics.Add($"family Family {index} audit", new InvalidOperationException("Unavailable."));
            data.Families.Add(new FamilyAuditFamily
            {
                Name = $"Family {index}", Status = "failed", Reason = "Unavailable."
            });
        }
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<FamilyAuditData>.Ok("family-audit", data, 1)));
            await Assert.That(json.RootElement.GetProperty("skippedCount").GetInt32()).IsEqualTo(125);
            await Assert.That(json.RootElement.GetProperty("skipped").GetArrayLength()).IsEqualTo(100);
        }
        finally { SkippedReadDiagnostics.Current = null; }
    }

    [Test]
    public async Task FailedRead_PreservesExceptionScopeAndReason()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("links-status read", new InvalidOperationException("Inspection failed."));
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<object>.Fail("links-status", "Inspection failed.", 1)));
            await Assert.That(json.RootElement.GetProperty("success").GetBoolean()).IsFalse();
            await Assert.That(json.RootElement.GetProperty("partial").GetBoolean()).IsTrue();
            await Assert.That(json.RootElement.GetProperty("skippedCount").GetInt32()).IsEqualTo(1);
            await Assert.That(json.RootElement.GetProperty("skipped")[0].GetProperty("what").GetString())
                .IsEqualTo("links-status read");
        }
        finally { SkippedReadDiagnostics.Current = null; }
    }

    [Test]
    public async Task ApiLookup_PropagatesNestedFailuresAndResultLimit()
    {
        var data = new ApiLookupResult
        {
            Skipped = [new SkippedRead { What = "API documentation", Reason = "Unavailable." }],
            SkippedCount = 1,
            Truncated = true
        };
        using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<ApiLookupResult>.Ok("api-lookup", data, 1)));
        await Assert.That(json.RootElement.GetProperty("skippedCount").GetInt32()).IsEqualTo(2);
        await Assert.That(json.RootElement.GetProperty("partial").GetBoolean()).IsTrue();
        await Assert.That(json.RootElement.GetProperty("skipped")[1].GetProperty("what").GetString())
            .IsEqualTo("api-lookup results");
    }

    [Test]
    public async Task NonPagedCaps_ReportScopeAndOmittedEntryCount()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.AddOmitted("links-status RVT links", 101, 100);
        diagnostics.AddOmitted("shared-coordinates project locations", 110, 100);
        diagnostics.AddOmitted("shared-coordinates link instances", 100, 100);
        await Assert.That(diagnostics.Count).IsEqualTo(2);
        await Assert.That(diagnostics.Items[0].What).IsEqualTo("links-status RVT links");
        await Assert.That(diagnostics.Items[0].Reason).IsEqualTo("1 of 101 entries omitted; list limit is 100.");
        await Assert.That(diagnostics.Items[1].Reason).IsEqualTo("10 of 110 entries omitted; list limit is 100.");
    }

    [Test]
    public async Task Merge_PreservesExistingFailuresAndIndependentNestedDetails()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("reader", new InvalidOperationException("Unavailable."));
        diagnostics.Merge([new SkippedRead { What = "nested reader", Reason = "Unsupported." }], 1);
        await Assert.That(diagnostics.Count).IsEqualTo(2);
        await Assert.That(diagnostics.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Add_DeduplicatesMatchingScopeAndReason()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("family audit", new InvalidOperationException("Unavailable."));
        diagnostics.Add("family audit", new InvalidOperationException("Unavailable."));
        diagnostics.Add("family audit", new InvalidOperationException("Unsupported."));
        await Assert.That(diagnostics.Count).IsEqualTo(2);
        await Assert.That(diagnostics.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Add_RetainsFirstHundredAndCountsEveryFailure()
    {
        var diagnostics = new SkippedReadDiagnostics();
        for (var index = 0; index < 125; index++)
        {
            diagnostics.Add($"element {index} parameter Mark", new InvalidOperationException("Parameter is unavailable."));
        }

        await Assert.That(diagnostics.Items.Count).IsEqualTo(100);
        await Assert.That(diagnostics.Count).IsEqualTo(125);
        await Assert.That(diagnostics.Items[0].What).IsEqualTo("element 0 parameter Mark");
        await Assert.That(diagnostics.Items[^1].What).IsEqualTo("element 99 parameter Mark");
    }

    [Test]
    public async Task ReadResponses_ExposeRealisticReaderFamiliesAtTopLevel()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("element 123 category", new InvalidOperationException("Category is unavailable."));
        diagnostics.Add("view Level 1 category Walls visibility", new InvalidOperationException("Visibility is unavailable."));
        diagnostics.Add("element 123 parameter Mark", new InvalidOperationException("Parameter is unavailable."));
        diagnostics.Add("element 123 bounding box on view 45", new InvalidOperationException("Geometry is unavailable."));

        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<ViewElementsData>.Ok("view-elements", new ViewElementsData(), 1)));
            var root = json.RootElement;
            await Assert.That(root.GetProperty("skippedCount").GetInt32()).IsEqualTo(4);
            var skipped = root.GetProperty("skipped");
            await Assert.That(skipped.GetArrayLength()).IsEqualTo(4);
            await Assert.That(skipped[0].GetProperty("what").GetString()).IsEqualTo("element 123 category");
            await Assert.That(skipped[1].GetProperty("what").GetString()).IsEqualTo("view Level 1 category Walls visibility");
            await Assert.That(skipped[2].GetProperty("reason").GetString()).IsEqualTo("Parameter is unavailable.");
            await Assert.That(skipped[3].GetProperty("what").GetString()).IsEqualTo("element 123 bounding box on view 45");
        }
        finally
        {
            SkippedReadDiagnostics.Current = null;
        }
    }

    [Test]
    public async Task CompleteRead_EmitsEmptyDiagnostics()
    {
        SkippedReadDiagnostics.Current = new SkippedReadDiagnostics();
        try
        {
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<string>.Ok("ping", "pong", 1)));
            await Assert.That(json.RootElement.GetProperty("skipped").GetArrayLength()).IsEqualTo(0);
            await Assert.That(json.RootElement.GetProperty("skippedCount").GetInt32()).IsEqualTo(0);
            await Assert.That(json.RootElement.GetProperty("partial").GetBoolean()).IsFalse();
        }
        finally
        {
            SkippedReadDiagnostics.Current = null;
        }
    }

    [Test]
    public async Task PartialRead_PreservesDiagnostics()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("view Level 1 element 123", new InvalidOperationException("Unavailable"));
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<ViewElementsData>.PartialResult("view-elements", new ViewElementsData(), "Timed out.", 1)));
            await Assert.That(json.RootElement.GetProperty("partial").GetBoolean()).IsTrue();
            await Assert.That(json.RootElement.GetProperty("skippedCount").GetInt32()).IsEqualTo(1);
        }
        finally
        {
            SkippedReadDiagnostics.Current = null;
        }
    }

    [Test]
    public async Task ActionResponse_DoesNotAcquireReadDiagnostics()
    {
        using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
            CommandResponse<string>.Ok("set-parameter", "updated", 1)));
        await Assert.That(json.RootElement.TryGetProperty("skipped", out _)).IsFalse();
        await Assert.That(json.RootElement.TryGetProperty("skippedCount", out _)).IsFalse();
    }

    [Test]
    public async Task ModelHealth_UsesCommonShapeOutsideData()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("model health counts.rooms", new InvalidOperationException("Unavailable"));
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<ModelHealthData>.Ok("model-health", new ModelHealthData(), 1)));
            await Assert.That(json.RootElement.GetProperty("skipped")[0].GetProperty("what").GetString())
                .IsEqualTo("model health counts.rooms");
            await Assert.That(json.RootElement.GetProperty("data").TryGetProperty("skipped", out _)).IsFalse();
            await Assert.That(json.RootElement.GetProperty("skipped")[0].TryGetProperty("metric", out _)).IsFalse();
        }
        finally
        {
            SkippedReadDiagnostics.Current = null;
        }
    }

    [Test]
    public async Task LinksStatus_PreservesPerLinkErrorAndCommonDiagnostic()
    {
        var diagnostics = new SkippedReadDiagnostics();
        diagnostics.Add("RVT link type 10", new InvalidOperationException("Unavailable"));
        SkippedReadDiagnostics.Current = diagnostics;
        try
        {
            var data = new LinksStatusData
            {
                RvtLinks = [new RvtLinkStatus { TypeId = 10, Status = "Other", Error = "Unavailable" }]
            };
            using var json = JsonDocument.Parse(CommandResponseJsonSerializer.Serialize(
                CommandResponse<LinksStatusData>.Ok("links-status", data, 1)));
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("rvtLinks")[0]
                .GetProperty("error").GetString()).IsEqualTo("Unavailable");
            await Assert.That(json.RootElement.GetProperty("skipped")[0].GetProperty("reason").GetString())
                .IsEqualTo("Unavailable");
        }
        finally
        {
            SkippedReadDiagnostics.Current = null;
        }
    }
}
