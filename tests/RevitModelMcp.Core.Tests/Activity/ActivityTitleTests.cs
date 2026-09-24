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
