using System.ComponentModel;
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
    private bool _canUndo;
    private bool _isUndone = entry.Undone;
    private bool _isExpanded;
    private List<ElementSection>? _sections;

    public ActivityEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string Title { get; } = ActivityTitleBuilder.Build(entry, PaneText.Language);
    public string DayLabel { get; } = PaneText.Day(entry.Time.LocalDateTime.Date);
    public string TimeText => Entry.Time.LocalDateTime.ToString("HH:mm");
    public string TimeToolTip => Entry.Time.LocalDateTime.ToString("G");
    public string ClientName => Entry.ClientName;
    public Brush LaneBrush { get; } = ClientLane.For(entry.ClientName);
    public string DocumentTitle => Entry.Document;
    public bool IsFailed => Entry.State == "failed";
    public bool IsDryRun => Entry.DryRun || Entry.State == "dry_run";
    public string ErrorText => IsFailed ? Entry.Summary : string.Empty;
    public int ChangedCount => Entry.Changed.Count;
    public int CreatedCount => Entry.Created.Count;
    public int DeletedCount => Entry.Deleted.Count;
    public string ChangedChip => $"~{ChangedCount}";
    public string CreatedChip => $"+{CreatedCount}";
    public string DeletedChip => $"\u2212{DeletedCount}";
    public bool HasElements => ChangedCount + CreatedCount + DeletedCount > 0;
    public bool CanShowAll => ChangedCount + CreatedCount > 0;

    /// <summary>Set once a row is inserted after the first refresh, so only genuinely new rows animate in.</summary>
    public bool IsNew { get; set; }

    public bool IsUndone { get => _isUndone; private set => Set(ref _isUndone, value); }
    public bool CanUndo { get => _canUndo; private set => Set(ref _canUndo, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public List<ElementSection> Sections => _sections ??= BuildSections();

    public void Refresh(bool canUndo)
    {
        IsUndone = Entry.Undone;
        CanUndo = canUndo;
    }

    private List<ElementSection> BuildSections()
    {
        var sections = new List<ElementSection>();
        if (ChangedCount > 0) sections.Add(new ElementSection(PaneText.ChangedSection, Entry.Changed, Entry.Document, true));
        if (CreatedCount > 0) sections.Add(new ElementSection(PaneText.CreatedSection, Entry.Created, Entry.Document, true));
        if (DeletedCount > 0) sections.Add(new ElementSection(PaneText.DeletedSection, Entry.Deleted, Entry.Document, false));
        return sections;
    }
}

/// <summary>"ИЗМЕНЕНО 12" style group inside an expanded row; shows the first 50 elements until asked for more.</summary>
public sealed class ElementSection : Observable
{
    private const int InitialLimit = 50;
    private readonly List<ActivityElementRefView> _all;
    private bool _showAll;

    public ElementSection(string header, List<ActivityElementRef> refs, string documentTitle, bool selectable)
    {
        Header = header;
        _all = refs.Select(reference => new ActivityElementRefView(reference, documentTitle, selectable)).ToList();
    }

    public string Header { get; }
    public int Count => _all.Count;
    public IReadOnlyList<ActivityElementRefView> Items => _showAll || _all.Count <= InitialLimit ? _all : _all.GetRange(0, InitialLimit);
    public bool HasMore => !_showAll && _all.Count > InitialLimit;
    public string MoreText => PaneText.More(_all.Count - InitialLimit);

    public void ShowAll()
    {
        if (!Set(ref _showAll, true, nameof(Items))) return;
        Raise(nameof(HasMore));
    }
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
            var parts = new[] { Ref.Category, Ref.Name }.Where(part => !string.IsNullOrEmpty(part)).ToArray();
            if (parts.Length > 0) return string.Join(" · ", parts);
            return IsSelectable ? PaneText.Element : PaneText.DeletedElement;
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
