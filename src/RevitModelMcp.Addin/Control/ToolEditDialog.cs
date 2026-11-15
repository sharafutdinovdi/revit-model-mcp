using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Ui.Kit;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace RevitModelMcp.Control;

internal static class ToolEditDialog
{
    internal static ToolPresentation? Show(Window owner, string label, ToolPresentation presentation, string? error = null)
    {
        var dialog = new Window
        {
            Title = "Edit tool",
            Owner = owner,
            Icon = AppDialog.LoadAppIcon(),
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        KitTheme.Apply(dialog);
        var chosen = presentation;
        ToolPresentation? result = null;
        void Identify(DependencyObject control, string id, string name)
        {
            AutomationProperties.SetAutomationId(control, "Edit." + id);
            AutomationProperties.SetName(control, name);
        }
        TextBlock Caption(string text) => new() { Text = text, FontSize = 11.5, Foreground = (Brush)dialog.FindResource("Muted"), Margin = new Thickness(0, 12, 0, 4), TextWrapping = TextWrapping.Wrap };
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var save = new Button { Content = "Save", IsDefault = true, Style = (Style)dialog.FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0) };
        Identify(cancel, "Cancel", "Cancel"); Identify(save, "Save", "Save");
        footer.Children.Add(cancel); footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        var labelRow = new DockPanel();
        var preview = new Image { Source = ToolIcons.Load(chosen, 32, false), Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(preview, Dock.Left); labelRow.Children.Add(preview);
        var labelPanel = new StackPanel();
        var caption = Caption("Label"); caption.Margin = new Thickness(0, 0, 0, 4);
        labelPanel.Children.Add(caption);
        var box = new TextBox { Text = presentation.Label, MaxLength = 80 };
        Identify(box, "Label", "Label"); labelPanel.Children.Add(box); labelRow.Children.Add(labelPanel); content.Children.Add(labelRow);
        content.Children.Add(Caption("Icon"));
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        var load = new Button { Content = "Load PNG…" };
        var reset = new Button { Content = "Use default", Margin = new Thickness(8, 0, 0, 0) };
        Identify(load, "LoadIcon", "Load PNG…"); Identify(reset, "DefaultIcon", "Use default");
        choices.Children.Add(load); choices.Children.Add(reset); content.Children.Add(choices);
        var hint = Caption("PNG up to 256 KB."); hint.Margin = new Thickness(0, 4, 0, 12); content.Children.Add(hint);
        var own = new CheckBox { Content = "The tool manages its own transactions", IsChecked = presentation.TransactionMode == "group", FontSize = 12.5 };
        Identify(own, "OwnTransactions", "The tool manages its own transactions"); content.Children.Add(own);
        var transactionHint = Caption("Leave this off unless the tool was written to open its own transactions."); transactionHint.Margin = new Thickness(0, 4, 0, 0); content.Children.Add(transactionHint);
        var errorLine = new TextBlock { Text = error ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible };
        errorLine.SetResourceReference(TextBlock.ForegroundProperty, "Error"); Identify(errorLine, "Error", "Error"); content.Children.Add(errorLine);
        box.TextChanged += (_, _) =>
        {
            errorLine.Text = "";
            errorLine.Visibility = Visibility.Collapsed;
        };
        load.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "PNG image (*.png)|*.png", CheckFileExists = true };
            if (picker.ShowDialog(dialog) != true) return;
            try
            {
                chosen = ToolIcons.Import(picker.FileName, presentation);
                preview.Source = ToolIcons.Load(chosen, 32, false);
                errorLine.Text = ""; errorLine.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception) { errorLine.Text = exception.Message; errorLine.Visibility = Visibility.Visible; }
        };
        reset.Click += (_, _) => { chosen = chosen with { SmallPng = "", LargePng = "" }; preview.Source = ToolIcons.Load(chosen, 32, false); };
        save.Click += (_, _) =>
        {
            result = presentation with { Label = box.Text, SmallPng = chosen.SmallPng, LargePng = chosen.LargePng, TransactionMode = own.IsChecked == true ? "group" : "auto" };
            dialog.DialogResult = true;
        };
        root.Children.Add(content); dialog.Content = root;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? result : null;
    }
}
