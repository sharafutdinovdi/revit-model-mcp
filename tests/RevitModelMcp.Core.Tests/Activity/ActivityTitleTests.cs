using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class ActivityTitleTests
{
    [Test]
    [Arguments(1, "Сдвинут 1 элемент")]
    [Arguments(3, "Сдвинуты 3 элемента")]
    [Arguments(5, "Сдвинуты 5 элементов")]
    [Arguments(11, "Сдвинуты 11 элементов")]
    [Arguments(21, "Сдвинут 21 элемент")]
    [Arguments(112, "Сдвинуты 112 элементов")]
    public async Task Build_RussianMove_AgreesWithCount(int count, string expected)
    {
        var title = ActivityTitleBuilder.Build(Entry("move", changed: count), ActivityLanguage.Russian);
        await Assert.That(title).IsEqualTo(expected);
    }

    [Test]
    [Arguments(1, "Moved 1 element")]
    [Arguments(3, "Moved 3 elements")]
    public async Task Build_EnglishMove_PluralizesCount(int count, string expected)
    {
        var title = ActivityTitleBuilder.Build(Entry("move", changed: count), ActivityLanguage.English);
        await Assert.That(title).IsEqualTo(expected);
    }

    [Test]
    public async Task Build_CountsCreatedAndDeletedElements()
    {
        var title = ActivityTitleBuilder.Build(Entry("delete", deleted: 2), ActivityLanguage.Russian);
        await Assert.That(title).IsEqualTo("Удалены 2 элемента");
    }

    [Test]
    public async Task Build_WithoutRecordedElements_OmitsCount()
    {
        var title = ActivityTitleBuilder.Build(Entry("select"), ActivityLanguage.Russian);
        await Assert.That(title).IsEqualTo("Выделены элементы");
    }

    [Test]
    public async Task Build_DryRun_UsesProcessForm()
    {
        var entry = Entry("move", changed: 3, state: "dry_run");
        await Assert.That(ActivityTitleBuilder.Build(entry, ActivityLanguage.Russian)).IsEqualTo("Сдвиг 3 элементов");
        await Assert.That(ActivityTitleBuilder.Build(entry, ActivityLanguage.English)).IsEqualTo("Moving 3 elements");
    }

    [Test]
    public async Task Build_DryRunSingleElement_UsesGenitiveSingular()
    {
        var entry = Entry("move", changed: 1, state: "dry_run");
        await Assert.That(ActivityTitleBuilder.Build(entry, ActivityLanguage.Russian)).IsEqualTo("Сдвиг 1 элемента");
    }

    [Test]
    public async Task Build_Failed_UsesProcessFormWithoutErrorText()
    {
        var entry = Entry("align-link-datums", state: "failed");
        entry.Summary = "Link 'AR' is not loaded.";
        await Assert.That(ActivityTitleBuilder.Build(entry, ActivityLanguage.Russian)).IsEqualTo("Выравнивание осей по связи");
        await Assert.That(ActivityTitleBuilder.Build(entry, ActivityLanguage.English)).IsEqualTo("Aligning datums to link");
    }

    [Test]
    public async Task Build_FixedPhrase_IgnoresCount()
    {
        var title = ActivityTitleBuilder.Build(Entry("place-family", created: 1), ActivityLanguage.Russian);
        await Assert.That(title).IsEqualTo("Размещено семейство");
    }

    [Test]
    public async Task Build_UnknownCommand_NamesCommand()
    {
        var title = ActivityTitleBuilder.Build(Entry("purge-model"), ActivityLanguage.English);
        await Assert.That(title).IsEqualTo("Ran purge-model");
    }

    [Test]
    public async Task BuildRunning_EndsWithEllipsis()
    {
        await Assert.That(ActivityTitleBuilder.BuildRunning("align-link-datums", ActivityLanguage.Russian))
            .IsEqualTo("Выравнивание осей по связи…");
        await Assert.That(ActivityTitleBuilder.BuildRunning("move", ActivityLanguage.English)).IsEqualTo("Moving elements…");
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
