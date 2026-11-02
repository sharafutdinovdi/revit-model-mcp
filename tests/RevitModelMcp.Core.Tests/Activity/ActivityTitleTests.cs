using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class ActivityTitleTests
{
    [Test]
    [Arguments(1, "Moved 1 element")]
    [Arguments(3, "Moved 3 elements")]
    public async Task Build_Move_PluralizesCount(int count, string expected)
    {
        var title = ActivityTitleBuilder.Build(Entry("move", changed: count));
        await Assert.That(title).IsEqualTo(expected);
    }

    [Test]
    public async Task Build_CountsCreatedAndDeletedElements()
    {
        var title = ActivityTitleBuilder.Build(Entry("delete", deleted: 2));
        await Assert.That(title).IsEqualTo("Deleted 2 elements");
    }

    [Test]
    public async Task Build_UsesTrueTotalsBeyondStoredRefs()
    {
        var entry = Entry("move", changed: 2);
        entry.ChangedTotal = 7200;
        await Assert.That(ActivityTitleBuilder.Build(entry)).IsEqualTo("Moved 7200 elements");
    }

    [Test]
    public async Task Build_UsesActionCount_IgnoringDependentChanges()
    {
        // Moving one link instance also modifies the 26 dimensions that follow it; the title reads by
        // what the action targeted, not by everything Revit touched as a side effect.
        var entry = Entry("move", changed: 27);
        entry.ActionCount = 1;
        await Assert.That(ActivityTitleBuilder.Build(entry)).IsEqualTo("Moved 1 element");
    }

    [Test]
    public async Task Build_WithoutRecordedElements_OmitsCount()
    {
        var title = ActivityTitleBuilder.Build(Entry("select"));
        await Assert.That(title).IsEqualTo("Selected elements");
    }

    [Test]
    public async Task Build_DryRun_UsesProcessForm()
    {
        var entry = Entry("move", changed: 3, state: "dry_run");
        await Assert.That(ActivityTitleBuilder.Build(entry)).IsEqualTo("Moving 3 elements");
    }

    [Test]
    public async Task Build_Failed_UsesProcessFormWithoutErrorText()
    {
        var entry = Entry("align-link-datums", state: "failed");
        entry.Summary = "Link 'AR' is not loaded.";
        await Assert.That(ActivityTitleBuilder.Build(entry)).IsEqualTo("Aligning datums to link");
    }

    [Test]
    public async Task Build_FixedPhrase_IgnoresCount()
    {
        var title = ActivityTitleBuilder.Build(Entry("place-family", created: 1));
        await Assert.That(title).IsEqualTo("Placed a family");
    }

    [Test]
    public async Task Build_UnknownCommand_NamesCommand()
    {
        var title = ActivityTitleBuilder.Build(Entry("purge-model"));
        await Assert.That(title).IsEqualTo("Ran purge-model");
    }

    [Test]
    public async Task BuildRunning_EndsWithEllipsis()
    {
        await Assert.That(ActivityTitleBuilder.BuildRunning("align-link-datums")).IsEqualTo("Aligning datums to link…");
        await Assert.That(ActivityTitleBuilder.BuildRunning("move")).IsEqualTo("Moving elements…");
    }

    private static ActivityEntry Entry(string command, int changed = 0, int created = 0, int deleted = 0, string state = "done") =>
        new()
        {
            Command = command,
            State = state,
            DryRun = state == "dry_run",
            Changed = Refs(changed),
            Created = Refs(created),
            Deleted = Refs(deleted)
        };

    private static List<ActivityElementRef> Refs(int count) =>
        Enumerable.Range(1, count).Select(id => new ActivityElementRef { Id = id }).ToList();
}

public sealed class TabActivityTrackerTests
{
    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.Parse("2026-10-22T12:00:00Z");

        public void Advance(Func<DateTimeOffset, DateTimeOffset> step) => Now = step(Now);
    }

    private readonly TestClock _clock = new();

    private TabActivityTracker Tracker() => new(() => _clock.Now);

    [Test]
    public async Task Action_HighlightsImmediately_WithClientAndSummary()
    {
        var tracker = Tracker();
        tracker.Start("job", "document", "Codex", "move", "Moving elements…");
        await Assert.That(tracker.Snapshot().Single()).IsEqualTo(
            new TabActivityState("document", "Model MCP: Codex running Moving elements…"));
        await Assert.That(tracker.NextChange).IsNull();
    }

    [Test]
    [Arguments("document-info", null)]
    [Arguments("view-elements", null)]
    [Arguments("family-audit", null)]
    [Arguments("family-audit", "summary")]
    public async Task OrdinaryRead_NeverHighlights_EvenAfterOneSecond(string command, string? detail)
    {
        var tracker = Tracker();
        tracker.Start("job", "document", "Codex", command, detail: detail);
        _clock.Advance(now => now.AddSeconds(4));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
        await Assert.That(tracker.NextChange).IsNull();
        tracker.End("job");
        _clock.Advance(now => now.AddSeconds(5));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
        await Assert.That(tracker.NextChange).IsNull();
    }

    [Test]
    [Arguments("export-view", null)]
    [Arguments("export", null)]
    [Arguments("export-nwc", null)]
    [Arguments("process-models", null)]
    [Arguments("family-audit", "full")]
    [Arguments("model-snapshot", null)]
    [Arguments("batch-snapshot", null)]
    [Arguments("model-health", null)]
    [Arguments("open-document", null)]
    public async Task KnownLongCommand_HighlightsAtStart(string command, string? detail)
    {
        var tracker = Tracker();
        tracker.Start("job", "document", "Codex", command, detail: detail);
        await Assert.That(tracker.Snapshot().Single().ToolTip).IsEqualTo($"Model MCP: Codex running {command}");
        await Assert.That(tracker.NextChange).IsNull();
    }

    [Test]
    public async Task QuickAction_StaysVisibleForMinimumTime()
    {
        var tracker = Tracker();
        tracker.Start("job", "document", "Codex", "move", "Moving elements…");
        _clock.Advance(now => now.AddMilliseconds(100));
        tracker.End("job");
        await Assert.That(tracker.NextChange).IsEqualTo(TimeSpan.FromMilliseconds(1400));
        _clock.Advance(now => now.AddMilliseconds(1399));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(1);
        _clock.Advance(now => now.AddMilliseconds(1));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task LongAction_HasOneSecondTail()
    {
        var tracker = Tracker();
        tracker.Start("job", "document", "Codex", "move", "Moving elements…");
        _clock.Advance(now => now.AddSeconds(3));
        tracker.End("job");
        await Assert.That(tracker.NextChange).IsEqualTo(TimeSpan.FromSeconds(1));
        _clock.Advance(now => now.AddMilliseconds(999));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(1);
        _clock.Advance(now => now.AddMilliseconds(1));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(400, 600)]
    [Arguments(1200, 0)]
    public async Task DelayedCompletion_UsesFinalResponseDeadline(int completionDelayMs, int remainingMs)
    {
        var tracker = Tracker();
        tracker.Start("job", "document", "Codex", "move");
        tracker.MarkVisible("document");
        _clock.Advance(now => now.AddSeconds(3));
        var responseDeliveredAt = _clock.Now;
        _clock.Advance(now => now.AddMilliseconds(completionDelayMs));
        tracker.End("job", responseDeliveredAt);
        await Assert.That(tracker.NextChange).IsEqualTo(TimeSpan.FromMilliseconds(remainingMs));
        _clock.Advance(now => now.AddMilliseconds(remainingMs));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task DelayedCompletion_PreservesVisualMinimum()
    {
        var tracker = Tracker();
        tracker.Start("job", "document", "Codex", "move");
        tracker.MarkVisible("document");
        _clock.Advance(now => now.AddMilliseconds(100));
        var responseDeliveredAt = _clock.Now;
        _clock.Advance(now => now.AddMilliseconds(400));
        tracker.End("job", responseDeliveredAt);
        await Assert.That(tracker.NextChange).IsEqualTo(TimeSpan.FromSeconds(1));
        _clock.Advance(now => now.AddMilliseconds(999));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(1);
        _clock.Advance(now => now.AddMilliseconds(1));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task LongRead_MinimumStartsWhenShown()
    {
        var tracker = Tracker();
        tracker.Start("job", "document", "Codex", "model-snapshot");
        _clock.Advance(now => now.AddSeconds(1));
        tracker.MarkVisible("document");
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(1);
        tracker.End("job");
        _clock.Advance(now => now.AddMilliseconds(1499));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(1);
        _clock.Advance(now => now.AddMilliseconds(1));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task BackToBackJobs_KeepIndicatorThroughReadAndRenewTail()
    {
        var tracker = Tracker();
        tracker.Start("first", "document", "Codex", "move", "Moving elements…");
        _clock.Advance(now => now.AddSeconds(2));
        tracker.End("first");
        _clock.Advance(now => now.AddMilliseconds(900));
        tracker.Start("second", "document", "Claude", "document-info");
        _clock.Advance(now => now.AddMilliseconds(200));
        await Assert.That(tracker.Snapshot().Single().ToolTip).IsEqualTo("Model MCP: Claude running document-info");
        tracker.End("second");
        _clock.Advance(now => now.AddMilliseconds(999));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(1);
        _clock.Advance(now => now.AddMilliseconds(1));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task TwoDocuments_HaveIndependentLifetimes()
    {
        var tracker = Tracker();
        tracker.Start("first", "A", "Codex", "move", "Moving elements…");
        tracker.Start("second", "B", "Claude", "model-snapshot");
        _clock.Advance(now => now.AddSeconds(1));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(2);
        tracker.End("first");
        _clock.Advance(now => now.AddSeconds(1));
        await Assert.That(tracker.Snapshot().Single().DocumentId).IsEqualTo("B");
        tracker.End("second");
        _clock.Advance(now => now.AddSeconds(1));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task OverlappingJobs_EndOnlyAfterLastJob()
    {
        var tracker = Tracker();
        tracker.Start("first", "document", "Codex", "move", "Moving elements…");
        tracker.Start("second", "document", "Claude", "delete", "Deleting elements…");
        _clock.Advance(now => now.AddSeconds(2));
        tracker.End("second");
        await Assert.That(tracker.Snapshot().Single().ToolTip).IsEqualTo("Model MCP: Codex running Moving elements…");
        await Assert.That(tracker.NextChange).IsNull();
        tracker.End("first");
        _clock.Advance(now => now.AddSeconds(1));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Progress_FormatsSteps_AndKeepsReadLabel()
    {
        var tracker = Tracker();
        tracker.Start("action", "A", "Codex", "move", "Moving elements…");
        tracker.Progress("action", 2, 5, "Moving walls");
        await Assert.That(tracker.Snapshot().Single().ToolTip).IsEqualTo("Model MCP: Codex running Moving walls, step 2 of 5");
        tracker.Progress("action", 0, 5);
        await Assert.That(tracker.Snapshot().Single().ToolTip).IsEqualTo("Model MCP: Codex running Moving walls, step 0 of 5");
        tracker.Progress("action", null, 5);
        await Assert.That(tracker.Snapshot().Single().ToolTip).IsEqualTo("Model MCP: Codex running Moving walls");
        tracker.End("action");
        tracker.Start("read", "B", "Claude", "model-snapshot");
        tracker.Progress("read", 1, 3, "Ignored read summary");
        _clock.Advance(now => now.AddSeconds(1));
        await Assert.That(tracker.Snapshot().Single(state => state.DocumentId == "B").ToolTip)
            .IsEqualTo("Model MCP: Claude running model-snapshot, step 1 of 3");
    }

    [Test]
    public async Task DisabledSetting_ClearsActivity_AndAllowsReenable()
    {
        var tracker = Tracker();
        tracker.Start("action", "A", "Codex", "move", "Moving elements…");
        tracker.Start("read", "B", "Claude", "document-info");
        tracker.Configure(false);
        tracker.Start("disabled", "C", "Codex", "move", "Moving elements…");
        _clock.Advance(now => now.AddSeconds(10));
        tracker.Progress("action", 1, 2);
        tracker.End("action");
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
        await Assert.That(tracker.NextChange).IsNull();
        tracker.Configure(true);
        tracker.Start("enabled", "C", "Codex", "move", "Moving elements…");
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(1);
    }

    [Test]
    public async Task DelayedVisualApplication_StillShowsForMinimumTime()
    {
        var tracker = Tracker();
        tracker.Start("action", "document", "Codex", "move", "Moving elements…");
        _clock.Advance(now => now.AddSeconds(4));
        tracker.End("action");
        tracker.MarkVisible("document");
        _clock.Advance(now => now.AddSeconds(1));
        tracker.MarkVisible("document");
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(1);
        await Assert.That(tracker.NextChange).IsEqualTo(TimeSpan.FromMilliseconds(500));
        _clock.Advance(now => now.AddMilliseconds(500));
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task OrdinaryRead_EndAfterBlockedDispatcher_DoesNotHighlight()
    {
        var tracker = Tracker();
        tracker.Start("read", "document", "Codex", "document-info");
        _clock.Advance(now => now.AddSeconds(4));
        tracker.End("read");
        await Assert.That(tracker.Snapshot().Count).IsEqualTo(0);
        await Assert.That(tracker.NextChange).IsNull();
    }
}

public sealed class TabActivitySettingsTests
{
    [Test]
    public async Task Restore_KeepsAnotherOwnersReference_EvenWithTheSameColour()
    {
        var applied = new TestBrush("blue");
        var replacement = new TestBrush("blue");
        await Assert.That(TabActivitySettings.ShouldRestore(applied, applied)).IsTrue();
        await Assert.That(TabActivitySettings.ShouldRestore(replacement, applied)).IsFalse();
        await Assert.That(TabActivitySettings.ShouldRestore<TestBrush?>(null, applied)).IsFalse();
    }

    [Test]
    public async Task Restore_ValueProperties_RequireTheAppliedValue()
    {
        await Assert.That(TabActivitySettings.ShouldRestore(3, 3)).IsTrue();
        await Assert.That(TabActivitySettings.ShouldRestore(5, 3)).IsFalse();
    }

    private sealed class TestBrush(string colour)
    {
        public string Colour { get; } = colour;
    }
}
