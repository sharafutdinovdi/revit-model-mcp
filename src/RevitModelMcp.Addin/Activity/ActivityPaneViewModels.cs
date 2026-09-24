using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Activity;

/// <summary>Minimal <see cref="INotifyPropertyChanged"/> base for the pane's mutable view models.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Pane-level state: read-only bar and the live strip for the running or queued job.</summary>
public sealed class PaneState : Observable
{
    private bool _isReadOnly;
    private bool _hasLive;
    private bool _isRunning;
    private string _liveClient = string.Empty;
    private string _liveTitle = string.Empty;
    private Brush? _liveLane;
    private int _activeCount;

    public bool IsReadOnly { get => _isReadOnly; set => Set(ref _isReadOnly, value); }
    public bool HasLive { get => _hasLive; set => Set(ref _hasLive, value); }
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }
    public string LiveClient { get => _liveClient; set => Set(ref _liveClient, value); }
    public string LiveTitle { get => _liveTitle; set => Set(ref _liveTitle, value); }
    public Brush? LiveLane { get => _liveLane; set => Set(ref _liveLane, value); }
    public int ActiveCount { get => _activeCount; set => Set(ref _activeCount, value); }
}

/// <summary>Display-ready wrapper around one <see cref="ActivityEntry"/>; kept across refreshes so expansion survives.</summary>
public sealed class ActivityRowView(ActivityEntry entry) : Observable
{
    private const int InitialLimit = 100;
    private bool _canUndo;
    private bool _isUndone = entry.Undone;
    private bool _isExpanded;
    private bool _showAll;
    private string _notice = string.Empty;
    private List<object>? _allItems;

    public ActivityEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string Title { get; } = ActivityTitleBuilder.Build(entry);
    public string DayLabel { get; } = PaneText.Day(entry.Time);
    public string TimeText => Entry.Time.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string TimeToolTip => Entry.Time.LocalDateTime.ToString("MMM d, yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    public string ClientName => Entry.ClientName;
    public Brush LaneBrush { get; } = ClientLane.For(entry.ClientName);
    public string DocumentTitle => Entry.Document;
    public bool IsFailed => Entry.State == "failed";
    public bool IsDryRun => Entry.DryRun || Entry.State == "dry_run";
    public string ErrorText => IsFailed ? Entry.Summary : string.Empty;
    public int ChangedCount => Entry.ChangedCount;
    public int CreatedCount => Entry.CreatedCount;
    public int DeletedCount => Entry.DeletedCount;
    public string ChangedChip => $"~{ChangedCount}";
    public string CreatedChip => $"+{CreatedCount}";
    public string DeletedChip => $"\u2212{DeletedCount}";
    public string ChangedChipToolTip => $"{ChangedCount} changed";
    public string CreatedChipToolTip => $"{CreatedCount} created";
    public string DeletedChipToolTip => $"{DeletedCount} deleted";
    public bool HasElements => ChangedCount + CreatedCount + DeletedCount > 0;
    public bool CanShowAll => SelectableItems.Any();

    /// <summary>Set once a row is inserted after the first refresh, so only genuinely new rows animate in.</summary>
    public bool IsNew { get; set; }

    public bool IsUndone { get => _isUndone; private set => Set(ref _isUndone, value); }
    public bool CanUndo { get => _canUndo; private set => Set(ref _canUndo, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>Inline status under the element list: copy confirmation or why selection is unavailable.</summary>
    public string Notice { get => _notice; set => Set(ref _notice, value); }

    /// <summary>Section headers and element rows in one flat list; the first 100 elements until "Show all".</summary>
    public IReadOnlyList<object> Items
    {
        get
        {
            var all = _allItems ??= BuildItems();
            if (_showAll) return all;
            var shown = new List<object>();
            var elements = 0;
            foreach (var item in all)
            {
                if (item is ActivityElementRefView && ++elements > InitialLimit) break;
                shown.Add(item);
            }
            return shown;
        }
    }

    public int StoredElementCount => Entry.Changed.Count + Entry.Created.Count + Entry.Deleted.Count;
    public bool HasMore => !_showAll && StoredElementCount > InitialLimit;
    public string ShowAllText => PaneText.ShowAll(StoredElementCount);

    /// <summary>Elements that still exist in the model and can be selected in Revit.</summary>
    public IEnumerable<ActivityElementRefView> SelectableItems =>
        (_allItems ??= BuildItems()).OfType<ActivityElementRefView>().Where(item => item.IsSelectable);

    public IEnumerable<ActivityElementRefView> AllElementItems => (_allItems ??= BuildItems()).OfType<ActivityElementRefView>();

    public void ShowAll()
    {
        if (!Set(ref _showAll, true, nameof(Items))) return;
        Raise(nameof(HasMore));
    }

    public void Refresh(bool canUndo)
    {
        IsUndone = Entry.Undone;
        CanUndo = canUndo;
    }

    private List<object> BuildItems()
    {
        var items = new List<object>();
        // Created elements of a dry run were rolled back; deleted ones no longer exist after a real run.
        Add(PaneText.ChangedSection, ChangedCount, Entry.Changed, true, null);
        Add(PaneText.CreatedSection, CreatedCount, Entry.Created, !IsDryRun, PaneText.ProvisionalToolTip);
        Add(PaneText.DeletedSection, DeletedCount, Entry.Deleted, false, PaneText.DeletedItemToolTip);
        return items;

        void Add(string header, int total, List<ActivityElementRef> refs, bool selectable, string? reason)
        {
            if (total == 0) return;
            items.Add(new ElementSectionHeader(header, total));
            items.AddRange(refs.Select(reference => new ActivityElementRefView(reference, selectable, reason)));
        }
    }
}

/// <summary>"CHANGED 12" header between groups of an expanded row's element list; not selectable.</summary>
public sealed class ElementSectionHeader(string header, int count)
{
    public string Header { get; } = header;
    public int Count { get; } = count;
    public bool IsSelectable => false;
}

/// <summary>One element in an expanded activity entry; deleted and dry-run created elements are not selectable.</summary>
public sealed class ActivityElementRefView(ActivityElementRef reference, bool isSelectable, string? notSelectableReason)
{
    public ActivityElementRef Ref { get; } = reference;
    public bool IsSelectable { get; } = isSelectable;
    public string IdText => Ref.Id.ToString(CultureInfo.InvariantCulture);
    public string? ToolTip => IsSelectable ? Label : $"{Label}. {notSelectableReason}";

    public string Label
    {
        get
        {
            var parts = new[] { Ref.Category, Ref.Name }.Where(part => !string.IsNullOrEmpty(part)).ToArray();
            if (parts.Length > 0) return string.Join(" · ", parts);
            return Ref.Category is null && Ref.Name is null && !IsSelectable ? PaneText.DeletedElement : PaneText.Element;
        }
    }
}

/// <summary>Display-ready wrapper around one queued, waiting or running <see cref="ScheduledJob"/>.</summary>
public sealed class QueueRowView(ScheduledJob job)
{
    public ScheduledJob Job { get; } = job;
    public string ClientName => Job.ClientName;
    public Brush LaneBrush { get; } = ClientLane.For(job.ClientName);
    public string Command => Job.Command;
    public string PositionText => Job.State switch
    {
        JobState.Running => PaneText.Running,
        JobState.WaitingRevit => PaneText.WaitingRevit,
        _ => PaneText.QueuePosition(Job.Position)
    };
    public bool CanCancel => Job.State is JobState.Queued or JobState.WaitingRevit or JobState.Running;
}
