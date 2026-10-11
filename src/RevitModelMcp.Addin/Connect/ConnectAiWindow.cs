using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitModelMcp.Core.Connect;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;

namespace RevitModelMcp.Connect;

/// <summary>Small modal dialog listing the supported AI apps with an honest state and an Add or Remove button each.</summary>
internal sealed class ConnectAiWindow : Window
{
    private readonly McpClientConfigurator _configurator = new();
    private readonly IReadOnlyList<McpClientTarget> _targets = McpClientLocator.Discover(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private readonly StackPanel _rows = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public ConnectAiWindow()
    {
        Title = ConnectText.Title;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 12;
        Background = Brushes.White;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = ConnectText.Intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(_rows);
        root.Children.Add(_message);
        var close = new Button { Content = ConnectText.Close, MinWidth = 80, Padding = new Thickness(12, 4, 12, 4), IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;
        Refresh();
    }

    private void Refresh()
    {
        _rows.Children.Clear();
        foreach (var target in _targets) _rows.Children.Add(Row(_configurator.Inspect(target)));
        _rows.Children.Add(ClaudeCodeRow());
    }

    private UIElement Row(McpClientStatus status)
    {
        var target = status.Target;
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = target.DisplayName, FontWeight = FontWeights.SemiBold });
        details.Children.Add(new TextBlock { Text = ConnectText.State(status.State), Foreground = StateBrush(status.State) });
        details.Children.Add(new TextBlock
        {
            Text = status.State == McpClientState.Unreadable && status.Detail is not null ? status.Detail : target.ConfigPath,
            Foreground = Brushes.Gray,
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = target.ConfigPath
        });
        var configured = status.State == McpClientState.Configured;
        var button = new Button
        {
            Content = configured ? ConnectText.Remove : ConnectText.Add,
            MinWidth = 92,
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = status.State is McpClientState.NotConfigured or McpClientState.Configured
        };
        button.Click += (_, _) => Apply(target, add: !configured);
        return Line(details, button);
    }

    private UIElement ClaudeCodeRow()
    {
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = ConnectText.ClaudeCode, FontWeight = FontWeights.SemiBold });
        details.Children.Add(new TextBlock { Text = ConnectText.ClaudeCodeHint, Foreground = Brushes.Gray });
        details.Children.Add(new TextBox
        {
            Text = McpServerSpec.ClaudeCodeCommand,
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 10.5,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        var button = new Button { Content = ConnectText.CopyCommand, MinWidth = 92, Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => Copy();
        return Line(details, button);
    }

    private static UIElement Line(UIElement details, UIElement button)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(button, 1);
        ((FrameworkElement)button).Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(details);
        grid.Children.Add(button);
        return grid;
    }

    private void Apply(McpClientTarget target, bool add)
    {
        try
        {
            var change = add ? _configurator.Add(target) : _configurator.Remove(target);
            _message.Foreground = Brushes.Black;
            _message.Text = !change.Changed
                ? ConnectText.Unchanged(target.DisplayName)
                : add ? ConnectText.Added(target.DisplayName, change.BackupPath) : ConnectText.Removed(target.DisplayName, change.BackupPath);
            PluginLog.Info($"Connect AI app: {(add ? "added" : "removed")} for '{target.Id}'. Changed={change.Changed}.");
        }
        catch (McpConfigException exception)
        {
            _message.Foreground = StateBrush(McpClientState.Unreadable);
            _message.Text = ConnectText.Failed(target.DisplayName, exception.Message);
            PluginLog.Warn($"Connect AI app failed for '{target.Id}'.");
        }
        Refresh();
    }

    private void Copy()
    {
        try
        {
            Clipboard.SetText(McpServerSpec.ClaudeCodeCommand);
            _message.Foreground = Brushes.Black;
            _message.Text = ConnectText.Copied;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _message.Foreground = StateBrush(McpClientState.Unreadable);
            _message.Text = ConnectText.CopyFailed;
        }
    }

    private static Brush StateBrush(McpClientState state) => state switch
    {
        McpClientState.Configured => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
        McpClientState.NotConfigured => new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00)),
        McpClientState.Unreadable => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
        _ => Brushes.Gray
    };
}
