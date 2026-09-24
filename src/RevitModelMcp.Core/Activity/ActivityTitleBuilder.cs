namespace RevitModelMcp.Core.Activity;

/// <summary>Language of the activity pane chrome and row titles; follows the Revit UI language.</summary>
public enum ActivityLanguage
{
    English,
    Russian
}

/// <summary>
/// Builds the short, localized row titles the activity pane shows. Finished changes read in the past tense
/// with element counts ("Moved 3 elements", "Сдвинуты 3 элемента"); dry runs, failures and running jobs read
/// as the process ("Moving 3 elements", "Сдвиг 3 элементов"). The English API <c>summary</c> is unaffected.
/// </summary>
public static class ActivityTitleBuilder
{
    private static readonly Noun Element = new("element", "elements", "элемент", "элемента", "элементов", "элементы", "элемента", "элементов");
    private static readonly Noun Family = new("family", "families", "семейство", "семейства", "семейств", "семейства", "семейства", "семейств");
    private static readonly Noun Link = new("link", "links", "связь", "связи", "связей", "связи", "связи", "связей");

    private static readonly Dictionary<string, Phrase> Phrases = new(StringComparer.Ordinal)
    {
        ["select"] = new("Selected", "Selecting", "Выделен", "Выделены", "Выделение", Element),
        ["show"] = new("Showed", "Showing", "Показан", "Показаны", "Показ", Element),
        ["isolate"] = new("Isolated", "Isolating", "Изолирован", "Изолированы", "Изоляция", Element),
        ["move"] = new("Moved", "Moving", "Сдвинут", "Сдвинуты", "Сдвиг", Element),
        ["delete"] = new("Deleted", "Deleting", "Удалён", "Удалены", "Удаление", Element),
        ["edit-families"] = new("Edited", "Editing", "Изменено", "Изменены", "Изменение", Family),
        ["remove-links"] = new("Removed", "Removing", "Удалена", "Удалены", "Удаление", Link),
        ["place-family"] = Fixed("Placed a family", "Placing a family", "Размещено семейство", "Размещение семейства"),
        ["create-wall"] = Fixed("Created a wall", "Creating a wall", "Создана стена", "Создание стены"),
        ["set-parameter"] = Fixed("Set a parameter", "Setting a parameter", "Изменён параметр", "Изменение параметра"),
        ["batch"] = Fixed("Ran a batch", "Running a batch", "Выполнен пакет команд", "Выполнение пакета команд"),
        ["export-nwc"] = Fixed("Exported NWC", "Exporting NWC", "Экспортирован NWC", "Экспорт NWC"),
        ["align-link-datums"] = Fixed("Aligned datums to link", "Aligning datums to link", "Выровнены оси по связи", "Выравнивание осей по связи"),
        ["undo-last"] = Fixed("Undid the last action", "Undoing the last action", "Отменено последнее действие", "Отмена последнего действия"),
        ["open-document"] = Fixed("Opened the document", "Opening the document", "Открыт документ", "Открытие документа"),
        ["close-document"] = Fixed("Closed the document", "Closing the document", "Закрыт документ", "Закрытие документа"),
        ["save-document"] = Fixed("Saved the document", "Saving the document", "Сохранён документ", "Сохранение документа"),
        ["sync-document"] = Fixed("Synchronized with central", "Synchronizing with central", "Синхронизировано с хранилищем", "Синхронизация с хранилищем"),
        ["set-view-visibility"] = Fixed("Changed view visibility", "Changing view visibility", "Изменена видимость на виде", "Изменение видимости на виде")
    };

    /// <summary>Title of a recorded activity row; counts are the elements the entry touched.</summary>
    public static string Build(ActivityEntry entry, ActivityLanguage language)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        var count = entry.Changed.Count + entry.Created.Count + entry.Deleted.Count;
        var finished = !entry.DryRun && entry.State is not ("failed" or "dry_run" or "queued" or "running");
        return Format(entry.Command, count, finished, language);
    }

    /// <summary>Title of a job that is still running or waiting in the queue, ending with an ellipsis.</summary>
    public static string BuildRunning(string command, ActivityLanguage language) =>
        Format(command, 0, false, language) + "…";

    private static string Format(string command, int count, bool finished, ActivityLanguage language)
    {
        var russian = language == ActivityLanguage.Russian;
        if (!Phrases.TryGetValue(command ?? string.Empty, out var phrase))
        {
            return (russian, finished) switch
            {
                (true, true) => $"Выполнена команда {command}",
                (true, false) => $"Команда {command}",
                (false, true) => $"Ran {command}",
                _ => $"Running {command}"
            };
        }

        if (phrase.Noun is not { } noun)
            return russian ? finished ? phrase.RuPastOne : phrase.RuProcess : finished ? phrase.EnPast : phrase.EnProcess;

        if (russian)
        {
            if (!finished)
                return count > 0 ? $"{phrase.RuProcess} {count} {noun.Genitive(count)}" : $"{phrase.RuProcess} {noun.RuGenitiveMany}";
            if (count == 0) return $"{phrase.RuPastMany} {noun.RuNominativePlural}";
            var verb = IsRussianSingular(count) ? phrase.RuPastOne : phrase.RuPastMany;
            return $"{verb} {count} {noun.Nominative(count)}";
        }

        var english = finished ? phrase.EnPast : phrase.EnProcess;
        if (count == 0) return $"{english} {noun.EnMany}";
        return $"{english} {count} {(count == 1 ? noun.EnOne : noun.EnMany)}";
    }

    private static bool IsRussianSingular(int count) => count % 10 == 1 && count % 100 != 11;

    private static bool IsRussianFew(int count) => count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14;

    private static Phrase Fixed(string enPast, string enProcess, string ruPast, string ruProcess) =>
        new(enPast, enProcess, ruPast, ruPast, ruProcess, null);

    private sealed record Phrase(string EnPast, string EnProcess, string RuPastOne, string RuPastMany, string RuProcess, Noun? Noun);

    private sealed record Noun(
        string EnOne, string EnMany,
        string RuOne, string RuFew, string RuMany, string RuNominativePlural,
        string RuGenitiveOne, string RuGenitiveMany)
    {
        public string Nominative(int count) =>
            IsRussianSingular(count) ? RuOne : IsRussianFew(count) ? RuFew : RuMany;

        public string Genitive(int count) => IsRussianSingular(count) ? RuGenitiveOne : RuGenitiveMany;
    }
}
