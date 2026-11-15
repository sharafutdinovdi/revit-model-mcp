using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Policy;
using RevitModelMcp.Ui.Kit;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Grid = System.Windows.Controls.Grid;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;

namespace RevitModelMcp.Control;

public partial class ToolManagerWindow : Window
{
    private readonly UIApplication _application;
    private List<ToolRow> _rows = [];
    private List<string> _slots = [];
    private string? _expanded;
    private readonly List<TextBlock> _descriptions = [];
    private sealed record ToolRow(string Name, ToolDefinition? Tool, ToolPresentation Presentation, string? Reason = null)
    {
        internal string Label => Tool is null ? Name : Presentation.Label;
        internal string Description => Tool?.Description ?? Reason ?? "";
    }

    internal ToolManagerWindow(UIApplication application)
    {
        _application = application;
        InitializeComponent();
        Icon = AppDialog.LoadAppIcon();
        KitTheme.Apply(this);
        Loaded += (_, _) => Refresh();
        SizeChanged += (_, _) => UpdateDescriptionVisibility();
        PreviewKeyDown += WindowKeyDown;
    }

    private static void Identify(DependencyObject control, string id, string name)
    {
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, name);
    }

    private void SetNotice(string text = "", bool error = false)
    {
        Notice.Text = text;
        Notice.Visibility = text.Length == 0 ? WpfVisibility.Collapsed : WpfVisibility.Visible;
        Notice.SetResourceReference(TextBlock.ForegroundProperty, error ? "Error" : "Muted");
    }

    private void Execute(Action action)
    {
        SetNotice();
        try { action(); }
        catch (Exception exception) { SetNotice(exception.Message, true); }
    }

    private static string? EditRefusal(string? policy = null)
    {
        ActionCommandExecutor.ReadOnlyLock.Refresh();
        if (ActionCommandExecutor.ReadOnlyMode) return PolicyProvider.ReadOnlyMessage;
        return policy is null ? null : PolicyProvider.Refusal(policy);
    }

    private static void EditGate(string? policy = null)
    {
        if (EditRefusal(policy) is string refusal) throw new InvalidOperationException(refusal);
    }

    private void Refresh()
    {
        try
        {
            if (PolicyProvider.Refusal("list-tools") is string refusal) throw new InvalidOperationException(refusal);
            var tools = MyTools.Store.List(out var invalid);
            var presentations = tools.ToDictionary(tool => tool.Name, tool => MyTools.Presentation.Get(tool.Name, ToolArguments.Title(tool.Parameters)));
            _slots = ToolRibbonSlots.Assign(presentations.Keys, name => presentations[name]).ToList();
            _rows = _slots.Select(name => new ToolRow(name, tools.First(tool => tool.Name == name), presentations[name]))
                .Concat(tools.Where(tool => !_slots.Contains(tool.Name)).OrderBy(tool => presentations[tool.Name].Label, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(tool => tool.Name, StringComparer.Ordinal).Select(tool => new ToolRow(tool.Name, tool, presentations[tool.Name])))
                .Concat(invalid.Select(tool => new ToolRow(tool.Name, null, MyTools.Presentation.Get(tool.Name, null), tool.Reason))).ToList();
            var importRefusal = EditRefusal("save-tool");
            ImportButton.IsEnabled = EmptyImport.IsEnabled = importRefusal is null;
            ImportButton.ToolTip = EmptyImport.ToolTip = importRefusal;
            ExportButton.IsEnabled = tools.Count != 0 && PolicyProvider.Refusal("get-tool") is null;
            ExportButton.ToolTip = PolicyProvider.Refusal("get-tool");
            Render();
        }
        catch (Exception exception) { SetNotice("Tools could not be loaded. " + exception.Message, true); }
    }

    private TextBlock Text(string text, bool muted = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource(muted ? "Muted" : "Text"),
        VerticalAlignment = VerticalAlignment.Center
    };

    private Button Button(string text, Action action, string? id = null, string? style = null)
    {
        var button = new Button { Content = text };
        if (style is not null) button.Style = (Style)FindResource(style);
        if (id is not null) Identify(button, id, text);
        button.Click += (_, _) => Execute(action);
        return button;
    }

    private void Render(string? focus = null)
    {
        if (Rows is null) return;
        Rows.Children.Clear();
        _descriptions.Clear();
        var query = Search.Text.Trim();
        var filtered = _rows.Where(row => new[] { row.Name, row.Label, row.Description }
            .Any(value => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        var showIcons = filtered.Any(row => !string.IsNullOrEmpty(row.Presentation.SmallPng));
        foreach (var row in filtered) Rows.Children.Add(BuildRow(row, focus == row.Name, showIcons));
        EmptyState.Visibility = _rows.Count == 0 ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        NoMatches.Visibility = _rows.Count != 0 && filtered.Count == 0 ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        NoMatches.Text = $"No tools match \"{query}\"";
        UpdateDescriptionVisibility();
    }

    private void UpdateDescriptionVisibility()
    {
        foreach (var description in _descriptions) description.Visibility = ActualWidth < 620 ? WpfVisibility.Collapsed : WpfVisibility.Visible;
    }

    private Border BuildRow(ToolRow row, bool focus, bool showIcons)
    {
        var expanded = _expanded == row.Name;
        var body = new StackPanel();
        var outer = new Border
        {
            Child = body,
            Background = expanded ? (Brush)FindResource("Raised") : Brushes.Transparent,
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = expanded ? new Thickness(0, 1, 0, 1) : new Thickness(0)
        };
        var rail = new Border { Style = (Style)FindResource("RowHover"), BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(2, 0, 0, 0), Child = outer };
        if (!expanded) rail.BorderBrush = Brushes.Transparent;
        var line = new Grid { Height = 36, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var width in new[] { new GridLength(28), new GridLength(1, GridUnitType.Star), new GridLength(28) })
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var rowContent = new Grid();
        foreach (var width in new[] { new GridLength(showIcons ? 28 : 0), new GridLength(240), new GridLength(1, GridUnitType.Star), new GridLength(28) })
            rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var chevron = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(expanded ? "Icon.ChevronDown" : "Icon.ChevronRight"),
            Style = (Style)FindResource("KitGlyph"),
            Stroke = (Brush)FindResource("Faint"),
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(chevron, 3); rowContent.Children.Add(chevron);
        if (!string.IsNullOrEmpty(row.Presentation.SmallPng))
        {
            var icon = new Image { Width = 20, Height = 20, Source = ToolIcons.Load(row.Presentation, 32, false), HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            rowContent.Children.Add(icon);
        }
        var onRibbon = _slots.Contains(row.Name);
        var label = new TextBlock { Text = row.Label, FontSize = 13, FontWeight = onRibbon ? FontWeights.SemiBold : FontWeights.Normal, Foreground = (Brush)FindResource(onRibbon ? "Text" : "Muted"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = row.Label };
        Grid.SetColumn(label, 1); rowContent.Children.Add(label);
        var description = new TextBlock { Text = row.Description, Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        description.SetResourceReference(TextBlock.ForegroundProperty, row.Tool is null ? "Warning" : onRibbon ? "Muted" : "Faint");
        Grid.SetColumn(description, 2); rowContent.Children.Add(description); _descriptions.Add(description);
        var expand = Button(row.Label, () => { _expanded = expanded ? null : row.Name; Render(row.Name); }, "Tool." + row.Name, "QuietButton");
        expand.Content = rowContent;
        expand.Background = Brushes.Transparent;
        expand.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        expand.Padding = new Thickness(0);
        Grid.SetColumn(expand, 1); line.Children.Add(expand);
        if (focus) expand.Loaded += (_, _) => expand.Focus();
        var ribbon = new CheckBox { IsChecked = _slots.Contains(row.Name), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        Identify(ribbon, $"Tool.{row.Name}.Ribbon", "On ribbon: " + row.Label);
        var ribbonRefusal = EditRefusal() ?? (row.Tool is null ? row.Reason : !_slots.Contains(row.Name) && _slots.Count == ToolRibbonSlots.Capacity ? "The ribbon holds 24 tools. Take one off first." : null);
        ribbon.IsEnabled = ribbonRefusal is null;
        ribbon.ToolTip = ribbonRefusal ?? "Show on the ribbon";
        ToolTipService.SetShowOnDisabled(ribbon, true);
        void RibbonChanged(object sender, RoutedEventArgs args)
        {
            SetNotice();
            try
            {
                EditGate();
                if (ribbonRefusal is not null) throw new InvalidOperationException(ribbonRefusal);
                var on = ribbon.IsChecked == true;
                var order = _rows.Where(candidate => candidate.Tool is not null).Select(candidate => candidate.Presentation.Order).DefaultIfEmpty(-1).Max() + 1;
                Change(row, presentation => presentation with { Hidden = !on, Order = on ? order : presentation.Order });
            }
            catch (Exception exception)
            {
                Refresh();
                SetNotice(exception.Message, true);
            }
        }
        ribbon.Checked += RibbonChanged;
        ribbon.Unchecked += RibbonChanged;
        line.Children.Add(ribbon);
        Button menu = null!;
        menu = Button("⋯", () => OpenMenu(row, menu), $"Tool.{row.Name}.Menu", "IconButton");
        menu.Background = Brushes.Transparent;
        AutomationProperties.SetName(menu, "Actions for " + row.Label);
        Grid.SetColumn(menu, 2); line.Children.Add(menu);
        line.PreviewKeyDown += (_, args) => RowKeyDown(row, menu, args);
        outer.MouseRightButtonUp += (_, args) => { OpenMenu(row, menu); args.Handled = true; };
        body.Children.Add(new Border { Style = (Style)FindResource("RowHover"), Child = line });
        if (expanded)
        {
            var details = new StackPanel { Margin = new Thickness(36 + (showIcons ? 28 : 0), 4, 16, 12) };
            details.Children.Add(Text(row.Description));
            if (row.Tool is ToolDefinition tool)
            {
                foreach (var field in new ToolParameterForm(tool.Parameters).Fields)
                {
                    var parameter = Text("");
                    parameter.Inlines.Add(new Run(field.Label));
                    var descriptionSuffix = string.IsNullOrWhiteSpace(field.Description) ? "" : " · " + field.Description;
                    parameter.Inlines.Add(new Run($" {(field.Required ? "required" : "optional")}{descriptionSuffix}")
                    {
                        Foreground = (Brush)FindResource("Muted")
                    });
                    details.Children.Add(parameter);
                }
                var saved = DateTimeOffset.TryParse(tool.Created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    ? "Saved " + time.LocalDateTime.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("en")) : "";
                var mode = tool.Mode == "read" ? "Read only" : "Changes the model";
                var meta = Text(saved.Length == 0 ? mode : $"{mode} · {saved}", true);
                if (row.Presentation.TransactionMode == "group") meta.Text += " · Manages its own transactions";
                meta.FontSize = 11.5; meta.Margin = new Thickness(0, 8, 0, 8); details.Children.Add(meta);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                var code = Button("View code", () =>
                {
                    if (PolicyProvider.Refusal("get-tool") is string refusal) throw new InvalidOperationException(refusal);
                    new ToolCodeWindow(tool, row.Label) { Owner = this }.ShowDialog();
                }, $"Tool.{row.Name}.ViewCode");
                code.IsEnabled = PolicyProvider.Refusal("get-tool") is null;
                code.ToolTip = PolicyProvider.Refusal("get-tool");
                buttons.Children.Add(code); details.Children.Add(buttons);
            }
            body.Children.Add(details);
        }
        return rail;
    }


    private void Change(ToolRow row, Func<ToolPresentation, ToolPresentation> change)
    {
        EditGate();
        if (row.Tool is null) return;
        MyTools.Presentation.Save(row.Name, change(MyTools.Presentation.Get(row.Name, ToolArguments.Title(row.Tool.Parameters))));
        MyToolsRibbon.Refresh(); Refresh();
    }

    private void SaveOrder(IReadOnlyList<string> names)
    {
        EditGate();
        var remaining = _rows.Where(row => row.Tool is not null && !names.Contains(row.Name)).OrderBy(row => row.Presentation.Order).Select(row => row.Name);
        var ordered = names.Concat(remaining).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var row = _rows.First(candidate => candidate.Name == ordered[index]);
            MyTools.Presentation.Save(row.Name, row.Presentation with { Order = index });
        }
        MyToolsRibbon.Refresh();
    }

    private void Move(ToolRow row, int destination)
    {
        var ordered = _slots.ToList();
        if (!ordered.Remove(row.Name) || destination < 0 || destination > ordered.Count) return;
        ordered.Insert(destination, row.Name); SaveOrder(ordered); Refresh(); Render(row.Name);
    }

    private void Edit(ToolRow row)
    {
        EditGate();
        if (row.Tool is null) return;
        var current = MyTools.Presentation.Get(row.Name, ToolArguments.Title(row.Tool.Parameters));
        string? error = null;
        while (true)
        {
            var edited = ToolEditDialog.Show(this, row.Label, current, error);
            if (edited is null) return;
            EditGate();
            try { MyTools.Presentation.Save(row.Name, edited); }
            catch (ArgumentException exception)
            {
                current = edited;
                error = exception.Message;
                continue;
            }
            MyToolsRibbon.Refresh(); Refresh(); Render(row.Name);
            return;
        }
    }

    private void Delete(ToolRow row)
    {
        EditGate("delete-tool");
        var binding = MyTools.Store.DeletionBinding(row.Name);
        if (!KitDialog.Show($"Delete {row.Label}?", "This deletes the saved tool and all its revisions. Deletion cannot be undone.", "Delete tool", this, danger: true)) return;
        EditGate("delete-tool");
        MyTools.Store.Delete(row.Name, binding);
        var note = MyTools.Presentation.Delete(row.Name);
        MyToolsRibbon.Refresh(); Refresh();
        var summary = $"Deleted saved tool {row.Label}." + (note is null ? "" : " " + note);
        ActivityRecorder.RecordSystemNotice(summary); SetNotice(summary);
    }

    private void OpenMenu(ToolRow row, FrameworkElement target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        menu.Resources.MergedDictionaries.Add(Resources);
        MenuItem Item(string label, string id, Action action, string? refusal = null, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled && refusal is null, ToolTip = refusal };
            Identify(item, "Tool.Menu." + id, label);
            item.Click += (_, _) => Execute(action); menu.Items.Add(item); return item;
        }
        var valid = row.Tool is not null;
        Item("Run…", "Run", () => { RibbonToolRunner.Run(_application, row.Name, this); Refresh(); },
            EditRefusal("run-tool") ?? (_application.ActiveUIDocument is null ? "Open a document to run this tool." : null), valid);
        menu.Items.Add(new Separator());
        Item("Edit…", "Edit", () => Edit(row), EditRefusal(), valid);
        Item("Export…", "Export", () => Export(row.Name), PolicyProvider.Refusal("get-tool"), valid);
        var index = _slots.IndexOf(row.Name);
        if (index >= 0)
        {
            menu.Items.Add(new Separator());
            Item("Move up", "MoveUp", () => Move(row, index - 1), EditRefusal(), index > 0);
            Item("Move down", "MoveDown", () => Move(row, index + 1), EditRefusal(), index < _slots.Count - 1);
        }
        menu.Items.Add(new Separator());
        var delete = Item("Delete…", "Delete", () => Delete(row), EditRefusal("delete-tool"));
        delete.SetResourceReference(MenuItem.ForegroundProperty, "Error");
        menu.IsOpen = true;
    }

    private void RowKeyDown(ToolRow row, Button menu, KeyEventArgs args)
    {
        if (args.Key == Key.F2) { Execute(() => Edit(row)); args.Handled = true; }
        else if (args.Key == Key.Delete) { Execute(() => Delete(row)); args.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && (args.Key is Key.Up or Key.Down || args.SystemKey is Key.Up or Key.Down))
        {
            Execute(() => Move(row, _slots.IndexOf(row.Name) + (args.Key == Key.Up || args.SystemKey == Key.Up ? -1 : 1))); args.Handled = true;
        }
        else if (args.Key == Key.Apps) { OpenMenu(row, menu); args.Handled = true; }
    }


    private void WindowKeyDown(object sender, KeyEventArgs args)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.F) { Search.Focus(); Search.SelectAll(); args.Handled = true; }
        else if (args.Key == Key.Escape && Search.Text.Length != 0) { Search.Clear(); args.Handled = true; }
    }

    private void SearchChanged(object sender, TextChangedEventArgs args) => Render();
    private void Export(string? name = null) { SetNotice(ToolShareDialogs.Export(_application, this, name)); Refresh(); }
    private void ExportClick(object sender, RoutedEventArgs args) => Execute(() => Export());
    private void ImportClick(object sender, RoutedEventArgs args) => Execute(() =>
    {
        var result = ToolShareDialogs.Import(_application, this); Refresh(); SetNotice(result.Status);
    });
}
