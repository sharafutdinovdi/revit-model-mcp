using System.Globalization;
using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Activity;

/// <summary>Activity pane and ribbon text; English in every Revit UI language.</summary>
public static class PaneText
{
    public const string Caption = "MCP activity";
    public const string RibbonButton = "Activity";
    public const string RibbonToolTip =
        "Show or hide the MCP activity pane: what agents do in the model, what they changed, the queue and undo.";
    public const string Now = "Now";
    public const string WaitingRevit = "Waiting for Revit to be free";
    public const string Running = "running";
    public const string Cancel = "Cancel";
    public const string Undo = "Undo";
    public const string DryRunTag = "dry run";
    public const string UndoneTag = "undone";
    public const string FailedTag = "failed";
    public const string ReadOnly = "Read-only: agents cannot change the model";
    public const string ReadOnlyUndo = "Read-only mode is on.";
    public const string NoActiveDocument = "No active Revit document.";
    public const string EmptyTitle = "Everything agents do in the model shows up here.";
    public const string EmptyHint = "For example, ask: \"compare grids with the architectural link\".";
    public const string Element = "Element";
    public const string DeletedElement = "Deleted element";
    public const string SelectInRevit = "Select in Revit";
    public const string ZoomTo = "Zoom to";
    public const string CopyIds = "Copy IDs";
    public const string Isolate = "Isolate";
    public const string OpenDocumentToSelect = "Open this document to select elements";
    public const string NothingSelectable = "No existing elements to select";
    public const string ProvisionalToolTip = "Created by a dry run and rolled back; not selectable";
    public const string DeletedItemToolTip = "Deleted; not selectable";

    public static string ChangedSection => Spaced("Changed");
    public static string CreatedSection => Spaced("Created");
    public static string DeletedSection => Spaced("Deleted");

    public static string ShowAll(int count) => $"Show all {count}";

    public static string Copied(int count) => count == 1 ? "Copied 1 ID" : $"Copied {count} IDs";

    public static string QueuePosition(int position) => $"#{position} in queue";

    public static string Day(DateTimeOffset time) => Spaced(ActivityDayLabel.For(time, DateTimeOffset.Now));

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
