using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Activity;

/// <summary>Display-ready wrapper around one <see cref="ActivityEntry"/> for the activity pane's list.</summary>
public sealed class ActivityRowView(ActivityEntry entry, bool canUndo)
{
    public ActivityEntry Entry { get; } = entry;
    public string TimeText => Entry.Time.LocalDateTime.ToString("HH:mm:ss");
    public string ClientBadge => Entry.ClientName;
    public string Command => Entry.Command;
    public string DocumentTitle => Entry.Document;

    /// <summary>Human title shown on the row's first line; falls back to the raw command if no summary was recorded.</summary>
    public string Title => string.IsNullOrEmpty(Entry.Summary) ? Entry.Command : Entry.Summary;

    public string StatusGlyph => Entry.State switch
    {
        "queued" => "\u25CF",
        "running" => "\u25CF",
        "failed" => "\u2715",
        "dry_run" => "\u25CC",
        _ => Entry.Undone ? "\u21BA" : "\u2713"
    };

    public string StatusText => Entry.Undone ? "undone" : Entry.State.Replace('_', ' ');
    public bool IsFailed => Entry.State == "failed";
    public bool IsRunning => Entry.State == "running";
    public bool IsQueued => Entry.State == "queued";
    public bool IsActive => IsRunning || IsQueued;
    public bool IsDryRun => Entry.DryRun;
    public bool IsUndone => Entry.Undone;

    /// <summary>"3 changed, 1 created" style summary of touched elements; empty when none were recorded.</summary>
    public string ElementCountText
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Changed.Count > 0) parts.Add($"{Entry.Changed.Count} changed");
            if (Entry.Created.Count > 0) parts.Add($"{Entry.Created.Count} created");
            if (Entry.Deleted.Count > 0) parts.Add($"{Entry.Deleted.Count} deleted");
            return string.Join(", ", parts);
        }
    }

    /// <summary>Muted second line: document title, element counts, and dry-run/undone flags, "·"-joined.</summary>
    public string MetaText
    {
        get
        {
            var segments = new List<string>();
            if (!string.IsNullOrEmpty(DocumentTitle)) segments.Add(DocumentTitle);
            var counts = ElementCountText;
            if (!string.IsNullOrEmpty(counts)) segments.Add(counts);
            if (IsDryRun) segments.Add("dry run");
            if (IsUndone) segments.Add("undone");
            return string.Join("  ·  ", segments);
        }
    }

    public List<ActivityElementRefView> ChangedRefs => Wrap(Entry.Changed, true);
    public List<ActivityElementRefView> CreatedRefs => Wrap(Entry.Created, true);
    public List<ActivityElementRefView> DeletedRefs => Wrap(Entry.Deleted, false);
    public bool HasElements => Entry.Changed.Count > 0 || Entry.Created.Count > 0 || Entry.Deleted.Count > 0;
    public bool CanShowAll => Entry.Changed.Count > 0 || Entry.Created.Count > 0;
    public bool CanUndo { get; } = canUndo;

    private List<ActivityElementRefView> Wrap(List<ActivityElementRef> refs, bool selectable) =>
        refs.Select(reference => new ActivityElementRefView(reference, Entry.Document, selectable)).ToList();
}

/// <summary>One clickable (or, for deleted elements, inert) row inside an expanded activity entry.</summary>
public sealed class ActivityElementRefView(ActivityElementRef reference, string documentTitle, bool isSelectable)
{
    public ActivityElementRef Ref { get; } = reference;
    public string DocumentTitle { get; } = documentTitle;
    public bool IsSelectable { get; } = isSelectable;
    public string IdText => Ref.Id.ToString();

    public string Label
    {
        get
        {
            var text = "";
            if (!string.IsNullOrEmpty(Ref.Category)) text += Ref.Category;
            if (!string.IsNullOrEmpty(Ref.Name)) text += (text.Length > 0 ? "  " : "") + Ref.Name;
            return text;
        }
    }
}

/// <summary>Display-ready wrapper around one queued or running <see cref="ScheduledJob"/>.</summary>
public sealed class QueueRowView(ScheduledJob job)
{
    public ScheduledJob Job { get; } = job;
    public string ClientBadge => Job.ClientName;
    public string Command => Job.Command;
    public string PositionText => Job.State == JobState.Running ? "running" : $"queued #{Job.Position}";
    public bool IsRunning => Job.State == JobState.Running;
    public bool IsActive => IsRunning;
    public bool CanCancel => Job.State is JobState.Queued or JobState.WaitingRevit or JobState.Running;
}
