using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using RevitModelMcp.Control;

namespace RevitModelMcp.Ui.Kit;

internal static class KitDialog
{
    internal static bool Show(string heading, string text, string action, Window? owner = null, bool danger = false, IntPtr ownerHandle = default)
        => Display(heading, text, action, owner, danger, ownerHandle, false);

    internal static void Inform(string heading, string text, Window? owner = null, IntPtr ownerHandle = default)
        => Display(heading, text, "Close", owner, false, ownerHandle, true);

    private static bool Display(string heading, string text, string action, Window? owner, bool danger, IntPtr ownerHandle, bool inform)
    {
        var dialog = new Window
        {
            Title = heading,
            Owner = owner,
            Icon = AppDialog.LoadAppIcon(),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height * 0.85,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        if (owner is null && ownerHandle != IntPtr.Zero) new WindowInteropHelper(dialog).Owner = ownerHandle;
        KitTheme.Apply(dialog);
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = inform ? "Close" : "Cancel", IsDefault = true, IsCancel = true };
        AutomationProperties.SetAutomationId(cancel, inform ? "Confirm.Close" : "Confirm.Cancel");
        AutomationProperties.SetName(cancel, inform ? "Close" : "Cancel");
        cancel.Click += (_, _) => dialog.Close();
        footer.Children.Add(cancel);
        if (!inform)
        {
            var confirm = new Button { Content = action, Margin = new Thickness(8, 0, 0, 0), Style = (Style)dialog.FindResource(danger ? "DangerButton" : "PrimaryButton") };
            AutomationProperties.SetAutomationId(confirm, "Confirm.Action"); AutomationProperties.SetName(confirm, action);
            confirm.Click += (_, _) => dialog.DialogResult = true;
            footer.Children.Add(confirm);
        }
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = heading, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = root;
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(); };
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }
}
