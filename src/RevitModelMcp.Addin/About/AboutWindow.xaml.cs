using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using RevitModelMcp.Core.Help;
using RevitModelMcp.Updates;

namespace RevitModelMcp.About;

public partial class AboutWindow : Window
{
    public AboutWindow(string revitYear)
    {
        InitializeComponent();
        var version = FileVersionInfo.GetVersionInfo(typeof(AboutWindow).Assembly.Location).ProductVersion ?? "0.0.0";
        VersionText.Text = $"Version: {Core.Help.VersionText.Format(version)}";
        RevitText.Text = $"Revit: {revitYear}";
        ChannelText.Text = $"Update channel: {UpdateService.Channel().ToString().ToLowerInvariant()}";
        DocumentationLink.NavigateUri = new Uri(HelpLinks.Documentation);
        SourceLink.NavigateUri = new Uri(HelpLinks.Source);
        PrivacyLink.NavigateUri = new Uri(HelpLinks.Privacy);
        SupportLink.NavigateUri = new Uri(HelpLinks.Support);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        Close();
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs args)
    {
        Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
        args.Handled = true;
    }
}
