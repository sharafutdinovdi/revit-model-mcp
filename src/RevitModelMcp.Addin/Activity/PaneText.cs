using System.Globalization;
using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Activity;

/// <summary>
/// Activity pane chrome in the Revit UI language: Russian when Revit runs as <c>LanguageType.Russian</c>,
/// English otherwise. Set once during application startup, before the pane and ribbon are created.
/// </summary>
public static class PaneText
{
    public static ActivityLanguage Language { get; set; } = ActivityLanguage.English;

    private static bool Ru => Language == ActivityLanguage.Russian;

    public static string Caption => Ru ? "Журнал MCP" : "MCP activity";
    public static string RibbonButton => Ru ? "Журнал" : "Activity";
    public static string RibbonToolTip => Ru
        ? "Показать или скрыть журнал MCP: что агенты делают в модели, что изменили, очередь и откат."
        : "Show or hide the MCP activity pane: what agents do in the model, what they changed, the queue and undo.";
    public static string Now => Ru ? "Сейчас" : "Now";
    public static string WaitingRevit => Ru ? "Ждёт, пока Revit освободится" : "Waiting for Revit to be free";
    public static string Running => Ru ? "выполняется" : "running";
    public static string Cancel => Ru ? "Отменить" : "Cancel";
    public static string Show => Ru ? "Показать" : "Show";
    public static string Undo => Ru ? "Откатить" : "Undo";
    public static string DryRunTag => Ru ? "пробно" : "dry run";
    public static string UndoneTag => Ru ? "отменено" : "undone";
    public static string FailedTag => Ru ? "ошибка" : "failed";
    public static string ReadOnly => Ru ? "Только чтение: агенты не могут менять модель" : "Read-only: agents cannot change the model";
    public static string ReadOnlyUndo => Ru ? "Включён режим только чтения." : "Read-only mode is on.";
    public static string NoActiveDocument => Ru ? "Нет активного документа Revit." : "No active Revit document.";
    public static string EmptyTitle => Ru ? "Здесь появится всё, что агенты делают в модели." : "Everything agents do in the model shows up here.";
    public static string EmptyHint => Ru ? "Например, попросите: «сравни оси со связью АР»." : "For example, ask: \"compare grids with the architectural link\".";
    public static string ChangedSection => Spaced(Ru ? "Изменено" : "Changed");
    public static string CreatedSection => Spaced(Ru ? "Создано" : "Created");
    public static string DeletedSection => Spaced(Ru ? "Удалено" : "Deleted");
    public static string DeletedElement => Ru ? "Удалённый элемент" : "Deleted element";
    public static string Element => Ru ? "Элемент" : "Element";

    public static string More(int count) => Ru ? $"ещё {count}" : $"{count} more";

    public static string QueuePosition(int position) => Ru ? $"{position}-й в очереди" : $"#{position} in queue";

    public static string Day(DateTime day)
    {
        var today = DateTime.Today;
        var text = day == today ? Ru ? "Сегодня" : "Today"
            : day == today.AddDays(-1) ? Ru ? "Вчера" : "Yesterday"
            : day.ToString("d MMMM", CultureInfo.GetCultureInfo(Ru ? "ru-RU" : "en-US"));
        return Spaced(text);
    }

    /// <summary>Upper-cases a section label and separates its letters with hair spaces; WPF has no letter spacing.</summary>
    private static string Spaced(string text) =>
        string.Join("\u200A", text.ToUpper(CultureInfo.InvariantCulture).Select(character => character.ToString()));
}

/// <summary>Stable per-client lane colour, used only for the row stripe and the client name.</summary>
internal static class ClientLane
{
    private static readonly string[] Fallback = ["#D16D8A", "#9BB04B", "#C98A5A"];
    private static readonly Dictionary<string, System.Windows.Media.Brush> Cache = new(StringComparer.Ordinal);

    public static System.Windows.Media.Brush For(string? clientName)
    {
        var name = (clientName ?? string.Empty).Trim().ToLowerInvariant();
        if (Cache.TryGetValue(name, out var cached)) return cached;
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Hex(name)));
        brush.Freeze();
        Cache[name] = brush;
        return brush;
    }

    private static string Hex(string name)
    {
        if (name.StartsWith("claude", StringComparison.Ordinal)) return "#D9A441";
        if (name.StartsWith("codex", StringComparison.Ordinal)) return "#3AA9A0";
        if (name.StartsWith("omp", StringComparison.Ordinal)) return "#8B7CF6";
        if (name.StartsWith("cursor", StringComparison.Ordinal)) return "#4FA3D9";
        // FNV-1a: string.GetHashCode is randomized per process on .NET Core, so it is not stable.
        var hash = 2166136261u;
        foreach (var character in name) hash = (hash ^ character) * 16777619u;
        return Fallback[hash % (uint)Fallback.Length];
    }
}
