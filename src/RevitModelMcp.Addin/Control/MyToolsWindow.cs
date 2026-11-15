using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Toolkit.External;
using TextBox = System.Windows.Controls.TextBox;

namespace RevitModelMcp.Control;

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolsCommand : ExternalCommand
{
    public override void Execute()
    {
        var window = new ToolManagerWindow(Application);
        new WindowInteropHelper(window).Owner = Application.MainWindowHandle;
        try { window.ShowDialog(); }
        finally { MyToolsRibbon.Refresh(); }
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
        RevitModelMcp.Ui.Kit.KitTheme.Apply(dialog);
        dialog.Loaded += (_, _) => close.Focus();
        return dialog.ShowDialog() == true;
    }
}
