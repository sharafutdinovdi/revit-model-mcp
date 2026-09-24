using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace RevitModelMcp.Activity;

/// <summary>
/// Code-behind for the MCP activity dockable pane: polls <see cref="ActivityLog"/> and the job scheduler on
/// a timer (WPF cannot subscribe directly to Revit's ExternalEvent thread), keeps row view models stable
/// across refreshes, and dispatches selection and undo back into Revit through <see cref="ActivityHost.Dispatch"/>.
/// </summary>
public partial class ActivityPaneView : UserControl
{
    private static readonly Duration EnterDuration = TimeSpan.FromMilliseconds(160);
    private static readonly Duration ExpandDuration = TimeSpan.FromMilliseconds(140);

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ActivityRowView> _rowsById = new(StringComparer.Ordinal);
    private readonly TranslateTransform _sweepShift = new();
    private DateTime _day = DateTime.Today;
    private string _queueSignature = string.Empty;
    private bool _refreshedOnce;
    private bool _liveAnimating;

    public ObservableCollection<ActivityRowView> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    public ObservableCollection<QueueRowView> QueueRows { get; } = [];
    public PaneState State { get; } = new();

    public ActivityPaneView()
    {
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActivityRowView.DayLabel)));
        InitializeComponent();
        DataContext = this;
        ApplyTheme(PaneTheme.IsDark);
        PaneTheme.Changed += OnThemeChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
        _timer.Tick += (_, _) => Refresh();
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            PaneTheme.Changed -= OnThemeChanged;
        };
        Loaded += (_, _) =>
        {
            PaneTheme.Changed -= OnThemeChanged;
            PaneTheme.Changed += OnThemeChanged;
            ApplyTheme(PaneTheme.IsDark);
            _timer.Start();
        };
        _timer.Start();
        Refresh();
    }

    /// <summary>Windows "Show animations" setting; when off the pane has no motion at all.</summary>
    private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

    private void OnThemeChanged() => Dispatcher.Invoke(() => ApplyTheme(PaneTheme.IsDark));

    /// <summary>Copies the Dark or Light token variant from ActivityTheme.xaml into the pane's resources.</summary>
    private void ApplyTheme(bool isDark)
    {
        if (Resources[isDark ? "Dark" : "Light"] is not ResourceDictionary variant) return;
        foreach (DictionaryEntry token in variant) Resources[token.Key] = token.Value;

        var running = ((SolidColorBrush)variant["RunningBrush"]).Color;
        var clear = Color.FromArgb(0, running.R, running.G, running.B);
        Sweep.Fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(clear, 0.35),
                new(running, 0.5),
                new(clear, 0.65)
            },
            new Point(0, 0), new Point(1, 0)) { RelativeTransform = _sweepShift };
    }

    private void Refresh()
    {
        State.IsReadOnly = ActionCommandExecutor.ReadOnlyMode;
        RefreshRows();
        RefreshLive();
        _refreshedOnce = true;
    }

    private void RefreshRows()
    {
        if (DateTime.Today != _day)
        {
            // Day labels ("Today", "Yesterday") are baked into each row; rebuild them after midnight.
            _day = DateTime.Today;
            Rows.Clear();
            _rowsById.Clear();
        }

        var entries = ActivityLog.Snapshot();
        var present = new HashSet<string>(entries.Select(entry => entry.Id), StringComparer.Ordinal);
        for (var index = Rows.Count - 1; index >= 0; index--)
        {
            if (present.Contains(Rows[index].Id)) continue;
            _rowsById.Remove(Rows[index].Id);
            Rows.RemoveAt(index);
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!_rowsById.TryGetValue(entry.Id, out var row))
            {
                row = new ActivityRowView(entry) { IsNew = _refreshedOnce };
                _rowsById[entry.Id] = row;
                Rows.Insert(Math.Min(index, Rows.Count), row);
            }
            row.Refresh(index == 0 && !entry.Undone && !string.IsNullOrEmpty(entry.UndoEntryName));
        }
    }

    private void RefreshLive()
    {
        var jobs = (ActivityHost.Scheduler?.ActiveJobs() ?? [])
            .Where(job => job.State is JobState.Queued or JobState.WaitingRevit or JobState.Running)
            .ToList();

        var signature = string.Join("|", jobs.Select(job => $"{job.JobId}:{job.State}:{job.Position}"));
        if (signature != _queueSignature)
        {
            _queueSignature = signature;
            QueueRows.Clear();
            foreach (var job in jobs) QueueRows.Add(new QueueRowView(job));
        }

        var live = jobs.FirstOrDefault(job => job.State == JobState.Running) ?? jobs.FirstOrDefault();
        State.HasLive = live is not null;
        State.ActiveCount = jobs.Count;
        State.IsRunning = live?.State == JobState.Running;
        if (live is not null)
        {
            State.LiveClient = live.ClientName;
            State.LiveLane = ClientLane.For(live.ClientName);
            State.LiveTitle = live.State == JobState.WaitingRevit
                ? PaneText.WaitingRevit
                : ActivityTitleBuilder.BuildRunning(live.Command);
        }
        else
        {
            QueueToggle.IsChecked = false;
        }
        UpdateLiveMotion();
    }

    /// <summary>Pulses the live node (0.45-1 opacity, 1.2 s) and sweeps the progress line (1.4 s) while a job runs.</summary>
    private void UpdateLiveMotion()
    {
        var animate = State.IsRunning && MotionEnabled;
        if (animate == _liveAnimating) return;
        _liveAnimating = animate;
        if (!animate)
        {
            LiveNode.BeginAnimation(OpacityProperty, null);
            _sweepShift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        LiveNode.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
        _sweepShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-0.65, 0.65, TimeSpan.FromMilliseconds(1400))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    /// <summary>New rows fade in and slide down 8 px over 160 ms; rows present at first load do not animate.</summary>
    private void OnRowContainerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ActivityRowView { IsNew: true } row } item) return;
        row.IsNew = false;
        if (!MotionEnabled) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform();
        item.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, EnterDuration) { EasingFunction = ease });
        item.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, EnterDuration) { EasingFunction = ease });
    }

    private void OnRowHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActivityRowView row) Toggle(row);
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActivityRowView row) Toggle(row);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RowList.SelectedItem is not ActivityRowView row) return;
        Toggle(row);
        e.Handled = true;
    }

    /// <summary>Expands or collapses a row's element sections, animating their height over 140 ms.</summary>
    private void Toggle(ActivityRowView row)
    {
        if (!row.HasElements) return;
        var container = RowList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
        var details = container is null ? null : FindNamed(container, "Details");
        if (!MotionEnabled || details is null)
        {
            row.IsExpanded = !row.IsExpanded;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (!row.IsExpanded)
        {
            details.Height = 0;
            row.IsExpanded = true;
            details.Height = double.NaN;
            details.Measure(new Size(details.ActualWidth > 0 ? details.ActualWidth : container!.ActualWidth, double.PositiveInfinity));
            var target = details.DesiredSize.Height - details.Margin.Top - details.Margin.Bottom;
            var grow = new DoubleAnimation(0, Math.Max(target, 0), ExpandDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            grow.Completed += (_, _) => details.Height = double.NaN;
            details.BeginAnimation(HeightProperty, grow);
            return;
        }

        var shrink = new DoubleAnimation(details.ActualHeight, 0, ExpandDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        shrink.Completed += (_, _) =>
        {
            row.IsExpanded = false;
            details.Height = double.NaN;
        };
        details.Height = 0;
        details.BeginAnimation(HeightProperty, shrink);
    }

    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement { Name: var childName } element && childName == name) return element;
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    private void OnShowAllClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActivityRowView row) row.ShowAll();
    }

    /// <summary>Hover action on the row header: select and zoom to every element that still exists.</summary>
    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityRowView row) return;
        RunOnElements(row, row.SelectableItems.Select(item => item.Ref.Id).ToList(), ElementCommand.SelectAndZoom);
    }

    private void OnElementDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ActivityElementRefView { IsSelectable: true } reference } item) return;
        if (FindRow(item) is not { } row) return;
        RunOnElements(row, [reference.Ref.Id], ElementCommand.SelectAndZoom);
        e.Handled = true;
    }

    private void OnElementListKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox { DataContext: ActivityRowView row } list) return;
        if (e.Key == Key.Enter)
            RunOnElements(row, SelectableIds(row, list), ElementCommand.SelectAndZoom);
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            CopyIds(row, list);
        else return;
        e.Handled = true;
    }

    private void OnSelectClick(object sender, RoutedEventArgs e) => RunFromButton(sender, ElementCommand.Select);

    private void OnZoomClick(object sender, RoutedEventArgs e) => RunFromButton(sender, ElementCommand.Zoom);

    private void OnIsolateClick(object sender, RoutedEventArgs e) => RunFromButton(sender, ElementCommand.Isolate);

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityRowView row) return;
        CopyIds(row, ElementListFor((DependencyObject)sender));
    }

    private void RunFromButton(object sender, ElementCommand command)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityRowView row) return;
        RunOnElements(row, SelectableIds(row, ElementListFor((DependencyObject)sender)), command);
    }

    /// <summary>IDs of the selected selectable items, or of every element that still exists when none are selected.</summary>
    private static List<long> SelectableIds(ActivityRowView row, ListBox? list)
    {
        var selected = list?.SelectedItems.OfType<ActivityElementRefView>().Where(item => item.IsSelectable).ToList() ?? [];
        return (selected.Count > 0 ? selected : row.SelectableItems).Select(item => item.Ref.Id).ToList();
    }

    /// <summary>Copies the selected IDs, or all listed IDs when none are selected, as a comma-separated list.</summary>
    private void CopyIds(ActivityRowView row, ListBox? list)
    {
        var selected = list?.SelectedItems.OfType<ActivityElementRefView>().ToList() ?? [];
        var ids = (selected.Count > 0 ? selected : row.AllElementItems).Select(item => item.IdText).ToList();
        if (ids.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(",", ids));
            ShowNotice(row, PaneText.Copied(ids.Count));
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            ShowNotice(row, exception.Message);
        }
    }

    /// <summary>
    /// Selects, zooms to or isolates elements through the ExternalEvent handler. Works only while the entry's
    /// document is the active document; otherwise the row explains why inline.
    /// </summary>
    private void RunOnElements(ActivityRowView row, List<long> ids, ElementCommand command)
    {
        if (ids.Count == 0)
        {
            ShowNotice(row, PaneText.NothingSelectable);
            return;
        }
        var documentTitle = row.DocumentTitle;
        ActivityHost.Dispatch(application =>
        {
            var uiDocument = application.ActiveUIDocument;
            if (uiDocument is null || (!string.IsNullOrEmpty(documentTitle) && uiDocument.Document.Title != documentTitle))
            {
                Notify(row, PaneText.OpenDocumentToSelect);
                return;
            }
            var document = uiDocument.Document;
            var elementIds = ids.Select(ActionCommandExecutor.CreateId).Where(id => document.GetElement(id) is not null).ToList();
            if (elementIds.Count == 0)
            {
                Notify(row, PaneText.NothingSelectable);
                return;
            }
            try
            {
                switch (command)
                {
                    case ElementCommand.Select:
                        uiDocument.Selection.SetElementIds(elementIds);
                        break;
                    case ElementCommand.SelectAndZoom:
                        uiDocument.Selection.SetElementIds(elementIds);
                        uiDocument.ShowElements(elementIds);
                        break;
                    case ElementCommand.Zoom:
                        uiDocument.ShowElements(elementIds);
                        break;
                    case ElementCommand.Isolate:
                        using (var transaction = new Autodesk.Revit.DB.Transaction(document, "Isolate MCP elements"))
                        {
                            transaction.Start();
                            uiDocument.ActiveView.IsolateElementsTemporary(elementIds);
                            transaction.Commit();
                        }
                        break;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException exception)
            {
                Notify(row, exception.Message);
            }
        });
    }

    private void Notify(ActivityRowView row, string text) => Dispatcher.InvokeAsync(() => ShowNotice(row, text));

    /// <summary>Shows a short inline message under the row's element list and clears it after 3 seconds.</summary>
    private void ShowNotice(ActivityRowView row, string text)
    {
        row.Notice = text;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (row.Notice == text) row.Notice = string.Empty;
        };
        timer.Start();
    }

    private static ListBox? ElementListFor(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { Name: "Details" } details)
                return FindNamed(details, "ElementList") as ListBox;
        return null;
    }

    private static ActivityRowView? FindRow(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ListBox { DataContext: ActivityRowView row }) return row;
        return null;
    }

    private enum ElementCommand
    {
        Select,
        SelectAndZoom,
        Zoom,
        Isolate
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (ActionCommandExecutor.ReadOnlyMode)
        {
            TaskDialog.Show(PaneText.Caption, PaneText.ReadOnlyUndo);
            return;
        }
        ActivityHost.Dispatch(application =>
        {
            var uiDocument = application.ActiveUIDocument;
            if (uiDocument is null)
            {
                TaskDialog.Show(PaneText.Caption, PaneText.NoActiveDocument);
                return;
            }
            try
            {
                ActionCommandExecutor.ExecuteUndoLast(uiDocument.Document, uiDocument);
            }
            catch (Exception exception)
            {
                TaskDialog.Show(PaneText.Caption, exception.Message);
            }
        });
    }

    private void OnCancelClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueRowView row) return;
        ActivityHost.CancelJob?.Invoke(row.Job.JobId, row.Job.ClientId);
    }
}
