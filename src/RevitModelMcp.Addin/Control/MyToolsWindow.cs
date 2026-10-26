using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Policy;
using Binding = System.Windows.Data.Binding;
using TextBox = System.Windows.Controls.TextBox;

namespace RevitModelMcp.Control;

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolsCommand : ExternalCommand
{
    public override void Execute()
    {
        var window = new MyToolsWindow();
        new WindowInteropHelper(window).Owner = Application.MainWindowHandle;
        window.ShowDialog();
    }
}

internal sealed class MyToolsWindow : Window
{
    private readonly DataGrid _list = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _showCode = new() { Content = "Show code", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _delete = new() { Content = "Delete", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };

    internal MyToolsWindow()
    {
        Title = "My tools";
        Width = 1000;
        Height = 550;
        MinWidth = 620;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_showCode);
        buttons.Children.Add(_delete);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80, Padding = new Thickness(12, 4, 12, 4) };
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        foreach (var (heading, property) in new[] { ("Name", "Name"), ("Description", "Description"), ("Mode", "Mode"), ("Revision", "RevisionShort"), ("Revit years", "Years"), ("Updated (UTC)", "Created") })
            _list.Columns.Add(new DataGridTextColumn { Header = heading, Binding = new Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(_list);
        Content = root;
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _showCode.Click += (_, _) => ShowCode();
        _delete.Click += (_, _) => Delete();
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        Loaded += (_, _) => Refresh();
    }

    private sealed record Row(ToolDefinition Tool)
    {
        public string Name => Tool.Name;
        public string Description => Tool.Description;
        public string Mode => Tool.Mode;
        public string RevisionShort => Tool.Revision[..12];
        public string Years => string.Join(", ", Tool.TestedRevitYears);
        public string Created => Tool.Created;
    }

    private void Refresh()
    {
        _list.ItemsSource = null;
        try
        {
            if (PolicyProvider.Refusal("list-tools") is string refusal) throw new InvalidOperationException(refusal);
            var tools = MyTools.Store.List();
            _list.ItemsSource = tools.Select(tool => new Row(tool)).ToList();
            _status.Text = tools.Count == 0
                ? "No saved tools yet. After a successful procedure, ask the AI app to save it as a tool. Review its code and confirm the save in the AI app."
                : "Select a tool to review its source or delete it. Run tools through the AI app.";
        }
        catch (Exception exception) { _status.Text = "Tools could not be loaded. " + exception.Message + " Review the local tools folder or ask the AI app to save the tool again."; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _showCode.IsEnabled = _list.SelectedItem is Row;
        _delete.IsEnabled = _list.SelectedItem is Row && !ActionCommandExecutor.ReadOnlyMode && PolicyProvider.Refusal("delete-tool") is null;
    }

    private void ShowCode()
    {
        if (_list.SelectedItem is not Row row) return;
        try
        {
            if (PolicyProvider.Refusal("get-tool") is string refusal) throw new InvalidOperationException(refusal);
            var tool = MyTools.Store.Get(row.Name);
            var viewer = new Window
            {
                Title = "Show code: " + row.Name,
                Owner = this,
                Width = 850,
                Height = 650,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox
                {
                    Text = $"{tool.Name}\nSHA-256: {tool.Revision}\nMode: {tool.Mode}\nParameters: {tool.Parameters}\n\n{tool.Code}",
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(12)
                }
            };
            viewer.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) viewer.Close(); };
            viewer.ShowDialog();
        }
        catch (Exception exception) { _status.Text = "Code could not be opened. " + exception.Message; }
    }

    private void Delete()
    {
        if (_list.SelectedItem is not Row row) return;
        try
        {
            ActionCommandExecutor.ReadOnlyLock.Refresh();
            if (ActionCommandExecutor.ReadOnlyMode) throw new InvalidOperationException(PolicyProvider.ReadOnlyMessage);
            if (PolicyProvider.Refusal("delete-tool") is string refusal) throw new InvalidOperationException(refusal);
            var tool = MyTools.Store.Get(row.Name);
            var dialog = new TaskDialog("My tools")
            {
                MainInstruction = $"Delete {row.Name}?",
                MainContent = "This deletes the saved tool and all its revisions. Deletion cannot be undone.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
                MainIcon = TaskDialogIcon.TaskDialogIconWarning
            };
            if (dialog.Show() != TaskDialogResult.Yes) return;
            MyTools.Store.Delete(row.Name, tool.ApprovalBinding());
            RevitModelMcp.Activity.ActivityRecorder.RecordSystemNotice($"Deleted saved tool {row.Name}.");
            Refresh();
        }
        catch (Exception exception) { _status.Text = "Tool could not be deleted. " + exception.Message; }
    }
}
