using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Activity;

/// <summary>
/// Code-behind for the "MCP activity" dockable pane: polls <see cref="ActivityLog"/> and the job
/// scheduler on a timer (WPF cannot subscribe directly to Revit's ExternalEvent thread) and dispatches
/// selection and undo back into Revit through <see cref="ActivityHost.Dispatch"/>.
/// </summary>
public partial class ActivityPaneView : UserControl
{
    private readonly DispatcherTimer _timer;

    public ObservableCollection<ActivityRowView> Rows { get; } = [];
    public ObservableCollection<QueueRowView> QueueRows { get; } = [];

    public ActivityPaneView()
    {
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
        _timer.Start();
        Refresh();
    }

    private void OnThemeChanged() => Dispatcher.Invoke(() => ApplyTheme(PaneTheme.IsDark));

    /// <summary>Overwrites the pane's brush resources to match Revit's current UI theme; never pure black.</summary>
    private void ApplyTheme(bool isDark)
    {
        if (isDark)
        {
            Resources["PaneBackgroundBrush"] = Brush("#FF2B2B2B");
            Resources["RowHoverBrush"] = Brush("#FF3A3A3D");
            Resources["PaneForegroundBrush"] = Brush("#FFE6E6E6");
            Resources["PaneMutedBrush"] = Brush("#FFAFAFAF");
            Resources["PaneBorderBrush"] = Brush("#FF454545");
            Resources["BadgeBackgroundBrush"] = Brush("#FF3E4A5E");
            Resources["BadgeForegroundBrush"] = Brush("#FFD7E3F4");
            Resources["AccentBrush"] = Brush("#FF4FA3FF");
            Resources["StatusDoneBrush"] = Brush("#FF66BB6A");
            Resources["StatusFailedBrush"] = Brush("#FFEF5350");
            Resources["StatusDryRunBrush"] = Brush("#FFBDBDBD");
            Resources["StatusActiveBrush"] = Brush("#FF4FA3FF");
        }
        else
        {
            Resources["PaneBackgroundBrush"] = Brush("#FFFAFAFA");
            Resources["RowHoverBrush"] = Brush("#FFF0F0F0");
            Resources["PaneForegroundBrush"] = Brush("#FF1F1F1F");
            Resources["PaneMutedBrush"] = Brush("#FF6E6E6E");
            Resources["PaneBorderBrush"] = Brush("#FFE0E0E0");
            Resources["BadgeBackgroundBrush"] = Brush("#FFE4E9F2");
            Resources["BadgeForegroundBrush"] = Brush("#FF34495E");
            Resources["AccentBrush"] = Brush("#FF2F80ED");
            Resources["StatusDoneBrush"] = Brush("#FF2E7D32");
            Resources["StatusFailedBrush"] = Brush("#FFC62828");
            Resources["StatusDryRunBrush"] = Brush("#FF9E9E9E");
            Resources["StatusActiveBrush"] = Brush("#FF2F80ED");
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private void Refresh()
    {
        ReadOnlyBadge.Visibility = ActionCommandExecutor.ReadOnlyMode
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        var entries = ActivityLog.Snapshot();
        Rows.Clear();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var canUndo = index == 0 && !entry.Undone && !string.IsNullOrEmpty(entry.UndoEntryName);
            Rows.Add(new ActivityRowView(entry, canUndo));
        }

        QueueRows.Clear();
        foreach (var job in ActivityHost.Scheduler?.ActiveJobs() ?? [])
        {
            if (job.State is JobState.Done or JobState.Failed or JobState.Cancelled) continue;
            QueueRows.Add(new QueueRowView(job));
        }
    }

    private void OnShowAllClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityRowView row) return;
        var ids = row.Entry.Changed.Concat(row.Entry.Created).Select(reference => reference.Id).ToList();
        if (ids.Count == 0) return;
        ShowElements(row.DocumentTitle, ids);
    }

    private void OnElementClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActivityElementRefView reference) return;
        if (!reference.IsSelectable) return;
        ShowElements(reference.DocumentTitle, [reference.Ref.Id]);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (ActionCommandExecutor.ReadOnlyMode)
        {
            TaskDialog.Show("MCP activity", "read-only mode");
            return;
        }
        ActivityHost.Dispatch(application =>
        {
            var uiDocument = application.ActiveUIDocument;
            if (uiDocument is null)
            {
                TaskDialog.Show("MCP activity", "No active Revit document.");
                return;
            }
            try
            {
                ActionCommandExecutor.ExecuteUndoLast(uiDocument.Document, uiDocument);
            }
            catch (Exception exception)
            {
                TaskDialog.Show("MCP activity", exception.Message);
            }
        });
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueRowView row) return;
        ActivityHost.CancelJob?.Invoke(row.Job.JobId, row.Job.ClientId);
    }

    private static void ShowElements(string documentTitle, List<long> ids)
    {
        ActivityHost.Dispatch(application =>
        {
            var uiDocument = application.ActiveUIDocument;
            if (uiDocument is null || (!string.IsNullOrEmpty(documentTitle) && uiDocument.Document.Title != documentTitle))
                return;
            var elementIds = ids.Select(ActionCommandExecutor.CreateId).ToList();
            uiDocument.Selection.SetElementIds(elementIds);
            uiDocument.ShowElements(elementIds);
        });
    }
}
