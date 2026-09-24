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
    public string Summary => Entry.Summary;
    public string DocumentTitle => Entry.Document;

    public string StatusGlyph => Entry.State switch
    {
        "queued" => "\u23F3",
        "running" => "\u25B6",
        "failed" => "\u2716",
        "dry_run" => "\u25CE",
        _ => Entry.Undone ? "\u21A9" : "\u2714"
    };

    public string StatusText => Entry.Undone ? "undone" : Entry.State.Replace('_', ' ');
    public bool IsFailed => Entry.State == "failed";

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

    public string Label
    {
        get
        {
            var text = Ref.Id.ToString();
            if (!string.IsNullOrEmpty(Ref.Category)) text += "  " + Ref.Category;
            if (!string.IsNullOrEmpty(Ref.Name)) text += "  " + Ref.Name;
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
    public bool CanCancel => Job.State is JobState.Queued or JobState.WaitingRevit or JobState.Running;
}
