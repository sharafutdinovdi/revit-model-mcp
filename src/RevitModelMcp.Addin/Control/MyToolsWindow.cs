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
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using WpfControl = System.Windows.Controls.Control;

namespace RevitModelMcp.Control;

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolsCommand : ExternalCommand
{
    public override void Execute()
    {
        var window = new MyToolsWindow(Application);
        new WindowInteropHelper(window).Owner = Application.MainWindowHandle;
        try { window.ShowDialog(); }
        finally { MyToolsRibbon.Refresh(); }
    }
}

internal sealed class MyToolsWindow : Window
{
    private readonly DataGrid _list = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _showCode = new() { Content = "Show code", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _delete = new() { Content = "Delete", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly UIApplication _application;
    private readonly Button _run = new() { Content = "Run" };
    private readonly Button _rename = new() { Content = "Rename" };
    private readonly Button _icon = new() { Content = "Icon" };
    private readonly Button _visibility = new() { Content = "Hide from ribbon" };

    internal MyToolsWindow(UIApplication application)
    {
        _application = application;
        FollowTheme(this);
        Title = "My tools";
        Icon = AppDialog.LoadAppIcon();
        Width = 1000;
        Height = 550;
        MinWidth = 620;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _run, _rename, _icon, _visibility })
        {
            button.MinWidth = 70;
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Padding = new Thickness(12, 4, 12, 4);
            buttons.Children.Add(button);
        }
        buttons.Children.Add(_showCode);
        buttons.Children.Add(_delete);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80, Padding = new Thickness(12, 4, 12, 4) };
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Grid.SetRow(_status, 0);
        root.Children.Add(_status);
        foreach (var (heading, property) in new[] { ("Name", "Name"), ("Description", "Description"), ("Mode", "Mode"), ("Revision", "RevisionShort"), ("Revit years", "Years"), ("Saved (UTC)", "Created"), ("Replacement", "Replacement") })
            _list.Columns.Add(new DataGridTextColumn { Header = heading, Binding = new Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);
        Content = root;
        _list.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "HairlineBrush");
        _list.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "HairlineBrush");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _showCode.Click += (_, _) => ShowCode();
        _delete.Click += (_, _) => Delete();
        _run.Click += (_, _) => { if (_list.SelectedItem is Row row) RibbonToolRunner.Run(_application, row.Name, this); };
        _rename.Click += (_, _) => Rename();
        _icon.Click += (_, _) => ChooseIcon();
        _visibility.Click += (_, _) => ChangePresentation(presentation => presentation with { Hidden = !presentation.Hidden });
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        Loaded += (_, _) => Refresh();
    }

    internal static void FollowTheme(Window window)
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
        foreach (var controlType in new[] { typeof(Button), typeof(TextBox), typeof(ComboBox), typeof(CheckBox), typeof(DataGrid),
                     typeof(DataGridRow), typeof(DataGridCell), typeof(DataGridColumnHeader) })
        {
            var style = new Style(controlType);
            style.Setters.Add(new Setter(WpfControl.BackgroundProperty, new DynamicResourceExtension("SurfaceRaisedBrush")));
            style.Setters.Add(new Setter(WpfControl.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
            style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, new DynamicResourceExtension("HairlineBrush")));
            if (controlType == typeof(Button))
            {
                var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                        <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="1" Padding="{TemplateBinding Padding}">
                            <ContentPresenter Content="{TemplateBinding Content}"
                                              ContentTemplate="{TemplateBinding ContentTemplate}"
                                              ContentStringFormat="{TemplateBinding ContentStringFormat}"
                                              TextElement.Foreground="{TemplateBinding Foreground}"
                                              HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
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
                style.Setters.Add(new Setter(MinHeightProperty, 30.0));
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
        public string Label => MyTools.Presentation.Get(Name).Label;
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
                : "Select a tool to run, review, customize or delete it. The ribbon shows up to 24 visible tools; all approved tools can run here.";
        }
        catch (Exception exception) { _status.Text = "Tools could not be loaded. " + exception.Message + " Review the local tools folder or ask the AI app to save the tool again."; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _showCode.IsEnabled = _list.SelectedItem is Row { Tool: not null };
        _delete.IsEnabled = _list.SelectedItem is Row && !ActionCommandExecutor.ReadOnlyMode && PolicyProvider.Refusal("delete-tool") is null;
        var valid = _list.SelectedItem is Row { Tool: not null };
        _run.IsEnabled = valid && _application.ActiveUIDocument is not null;
        _rename.IsEnabled = valid;
        _icon.IsEnabled = valid;
        _visibility.IsEnabled = valid;
        if (_list.SelectedItem is Row row)
            _visibility.Content = MyTools.Presentation.Get(row.Name).Hidden ? "Show on ribbon" : "Hide from ribbon";
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
                Icon = AppDialog.LoadAppIcon(),
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
            MyTools.Presentation.Delete(row.Name);
            RevitModelMcp.Activity.ActivityRecorder.RecordSystemNotice($"Deleted saved tool {row.Name}.");
            Refresh();
        }
        catch (Exception exception) { _status.Text = "Tool could not be deleted. " + exception.Message; }
    }

    private void ChangePresentation(Func<ToolPresentation, ToolPresentation> change)
    {
        if (_list.SelectedItem is not Row { Tool: not null } row) return;
        try
        {
            MyTools.Presentation.Save(row.Name, change(MyTools.Presentation.Get(row.Name)));
            MyToolsRibbon.Refresh();
            UpdateButtons();
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private void Rename()
    {
        if (_list.SelectedItem is not Row row) return;
        var dialog = new Window
        {
            Title = "Display label",
            Icon = AppDialog.LoadAppIcon(),
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(16) };
        var input = new TextBox
        {
            Text = MyTools.Presentation.Get(row.Name).Label,
            MaxLength = 80
        };
        System.Windows.Automation.AutomationProperties.SetName(input, "Display label");
        root.Children.Add(input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        root.Children.Add(error);
        var save = new Button
        {
            Content = "Save",
            IsDefault = true,
            Margin = new Thickness(0, 12, 0, 0)
        };
        save.Click += (_, _) =>
        {
            try
            {
                var presentation = MyTools.Presentation.Get(row.Name) with { Label = input.Text };
                MyTools.Presentation.Save(row.Name, presentation);
                MyToolsRibbon.Refresh();
                dialog.Close();
            }
            catch (Exception exception) { error.Text = exception.Message; }
        };
        root.Children.Add(save);
        dialog.Content = root;
        FollowTheme(dialog);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(); };
        dialog.ShowDialog();
    }

    private void ChooseIcon()
    {
        if (_list.SelectedItem is not Row row) return;
        var dialog = new Window
        {
            Title = "Tool icon",
            Icon = AppDialog.LoadAppIcon(),
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(16) };
        var choices = new ComboBox
        {
            ItemsSource = ToolPresentationStore.BuiltInIcons,
            SelectedItem = MyTools.Presentation.Get(row.Name).Icon
        };
        System.Windows.Automation.AutomationProperties.SetName(choices, "Built-in icon");
        root.Children.Add(choices);
        var save = new Button
        {
            Content = "Use built-in icon",
            Margin = new Thickness(0, 12, 0, 0)
        };
        save.Click += (_, _) =>
        {
            ChangePresentation(presentation => presentation with
            {
                Icon = (string)choices.SelectedItem,
                SmallPng = "",
                LargePng = ""
            });
            dialog.Close();
        };
        root.Children.Add(save);
        var browse = new Button
        {
            Content = "Load PNG (up to 256 KB)",
            Margin = new Thickness(0, 8, 0, 0)
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PNG image (*.png)|*.png",
                CheckFileExists = true
            };
            if (picker.ShowDialog(dialog) != true) return;
            try
            {
                var presentation = ToolIcons.Import(picker.FileName, MyTools.Presentation.Get(row.Name));
                MyTools.Presentation.Save(row.Name, presentation);
                MyToolsRibbon.Refresh();
                dialog.Close();
            }
            catch (Exception exception) { error.Text = exception.Message; }
        };
        root.Children.Add(browse);
        root.Children.Add(error);
        dialog.Content = root;
        FollowTheme(dialog);
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(); };
        dialog.ShowDialog();
    }
}

internal static class AppDialog
{
    internal static System.Windows.Media.Imaging.BitmapImage LoadAppIcon()
    {
        var icon = new System.Windows.Media.Imaging.BitmapImage();
        icon.BeginInit();
        icon.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        icon.UriSource = new Uri("pack://application:,,,/RevitModelMcp;component/Resources/Icons/Logo32.png");
        icon.EndInit();
        icon.Freeze();
        return icon;
    }

    internal static bool Show(string title, string message, bool confirm = false, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = title,
            Icon = AppDialog.LoadAppIcon(),
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
