using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Ui.Kit;

namespace RevitModelMcp.Control;

public partial class ToolCodeWindow : Window
{
    internal ToolCodeWindow(ToolDefinition tool, string label)
    {
        InitializeComponent();
        KitTheme.Apply(this);
        Icon = AppDialog.LoadAppIcon();
        Heading.Text = label;
        var saved = DateTimeOffset.TryParse(tool.Created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.LocalDateTime.ToString("dd MMM yyyy HH:mm", CultureInfo.GetCultureInfo("en")) : tool.Created;
        Metadata.Inlines.Add(new Run(tool.Name) { FontFamily = (FontFamily)FindResource("MonoFont") });
        Metadata.Inlines.Add(new Run($" · revision {tool.Revision[..12]} · tested in Revit {string.Join(", ", tool.TestedRevitYears)} · saved {saved}"));
        Source.Text = tool.Code;
    }

    private void CopyClick(object sender, RoutedEventArgs args)
    {
        try { Clipboard.SetText(Source.Text); Error.Text = ""; }
        catch (Exception exception) { Error.Text = exception.Message; }
    }
}
