using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Policy;
using Binding = System.Windows.Data.Binding;
using TextBox = System.Windows.Controls.TextBox;
using WpfControl = System.Windows.Controls.Control;

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
        FollowTheme(this);
        Title = "My tools";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/RevitModelMcp;component/Resources/Icons/Logo32.png"));
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
        foreach (var (heading, property) in new[] { ("Name", "Name"), ("Description", "Description"), ("Mode", "Mode"), ("Revision", "RevisionShort"), ("Revit years", "Years"), ("Saved (UTC)", "Created"), ("Replacement", "Replacement") })
            _list.Columns.Add(new DataGridTextColumn { Header = heading, Binding = new Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(_list);
        Content = root;
        _list.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "HairlineBrush");
        _list.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "HairlineBrush");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _showCode.Click += (_, _) => ShowCode();
        _delete.Click += (_, _) => Delete();
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        Loaded += (_, _) => Refresh();
    }

    private static void FollowTheme(Window window)
    {
        var palette = new ResourceDictionary
        {
            Source = new Uri("/RevitModelMcp;component/Activity/ActivityTheme.xaml", UriKind.Relative)
        };
        void ApplyTheme()
        {
            var variant = (ResourceDictionary)palette[PaneTheme.IsDark ? "Dark" : "Light"];
            foreach (DictionaryEntry token in variant) window.Resources[token.Key] = token.Value;
        }
        void ThemeChanged() => window.Dispatcher.Invoke(ApplyTheme);
        ApplyTheme();
        window.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        window.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        foreach (var controlType in new[] { typeof(Button), typeof(TextBox), typeof(DataGrid),
                     typeof(DataGridRow), typeof(DataGridCell), typeof(DataGridColumnHeader) })
        {
            var style = new Style(controlType);
            style.Setters.Add(new Setter(WpfControl.BackgroundProperty, new DynamicResourceExtension("SurfaceRaisedBrush")));
            style.Setters.Add(new Setter(WpfControl.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
            style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, new DynamicResourceExtension("HairlineBrush")));
            if (controlType == typeof(Button))
            {
                var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Control">
                        <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="1" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter Property="BorderBrush" Value="{DynamicResource RunningBrush}"/>
                            </Trigger>
                            <Trigger Property="IsKeyboardFocusWithin" Value="True">
                                <Setter Property="BorderBrush" Value="{DynamicResource RunningBrush}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Foreground" Value="{DynamicResource TextMutedBrush}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                    """);
                style.Setters.Add(new Setter(WpfControl.TemplateProperty, template));
            }
            if (controlType == typeof(DataGridRow) || controlType == typeof(DataGridCell))
            {
                var selected = new Trigger { Property = controlType == typeof(DataGridRow) ? DataGridRow.IsSelectedProperty : DataGridCell.IsSelectedProperty, Value = true };
                selected.Setters.Add(new Setter(WpfControl.BackgroundProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));
                selected.Setters.Add(new Setter(WpfControl.ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
                style.Triggers.Add(selected);
            }
            window.Resources[controlType] = style;
        }
        PaneTheme.Changed += ThemeChanged;
        window.Closed += (_, _) => PaneTheme.Changed -= ThemeChanged;
    }

    private sealed record Row(string Name, ToolDefinition? Tool, string? Reason = null)
    {
        public string Description => Tool?.Description ?? Reason ?? "";
        public string Mode => Tool?.Mode ?? "not approved";
        public string RevisionShort => Tool?.Revision[..12] ?? "not approved";
        public string Years => Tool is null ? "" : string.Join(", ", Tool.TestedRevitYears);
        public string Created => Tool?.Created ?? "";
        public string Replacement => Tool is null || string.IsNullOrEmpty(Tool.ReplacedRevision)
            ? "" : $"Replaced {Tool.ReplacedRevision[..12]} on {Tool.Created}";
    }

    private void Refresh()
    {
        _list.ItemsSource = null;
        try
        {
            if (PolicyProvider.Refusal("list-tools") is string refusal) throw new InvalidOperationException(refusal);
            var tools = MyTools.Store.List(out var invalid);
            _list.ItemsSource = tools.Select(tool => new Row(tool.Name, tool)).Concat(invalid.Select(tool => new Row(tool.Name, null, tool.Reason))).ToList();
            _status.Text = tools.Count == 0 && invalid.Count == 0
                ? "No saved tools yet. After a successful procedure, ask the AI app to save it as a tool. Review its code and confirm the save in the AI app."
                : "Select a tool to review its source or delete it. Run tools through the AI app.";
        }
        catch (Exception exception) { _status.Text = "Tools could not be loaded. " + exception.Message + " Review the local tools folder or ask the AI app to save the tool again."; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _showCode.IsEnabled = _list.SelectedItem is Row { Tool: not null };
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
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/RevitModelMcp;component/Resources/Icons/Logo32.png")),
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
            FollowTheme(viewer);
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
            var binding = MyTools.Store.DeletionBinding(row.Name);
            if (!AppDialog.Show("My tools",
                    $"Delete {row.Name}?\n\nThis deletes the saved tool and all its revisions. Deletion cannot be undone.",
                    confirm: true, owner: this)) return;
            MyTools.Store.Delete(row.Name, binding);
            RevitModelMcp.Activity.ActivityRecorder.RecordSystemNotice($"Deleted saved tool {row.Name}.");
            Refresh();
        }
        catch (Exception exception) { _status.Text = "Tool could not be deleted. " + exception.Message; }
    }
}

internal static class AppDialog
{
    internal static bool Show(string title, string message, bool confirm = false, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = title,
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/RevitModelMcp;component/Resources/Icons/Logo32.png")),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (owner is not null) dialog.Owner = owner;
        else new WindowInteropHelper(dialog).Owner = Autodesk.Windows.ComponentManager.ApplicationWindow;
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = Math.Min(480, SystemParameters.WorkArea.Height * 0.6),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        if (confirm)
        {
            var yes = new Button { Content = "Yes", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            yes.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(yes);
        }
        var close = new Button { Content = confirm ? "No" : "OK", MinWidth = 80, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(close);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => close.Focus();
        return dialog.ShowDialog() == true;
    }
}
