using RevitModelMcp.Core.Help;

namespace RevitModelMcp.Core.Tests.Help;

public sealed class HelpLinksTests
{
    [Test]
    public async Task DocumentationPointsToInstallPage()
    {
        var helpUri = new Uri(HelpLinks.Documentation, UriKind.Absolute);
        await Assert.That(helpUri.IsAbsoluteUri).IsTrue();
        await Assert.That(helpUri.Scheme).IsEqualTo("https");
        await Assert.That(helpUri.Host).IsEqualTo("sharafutdinovdi.github.io");
        await Assert.That(helpUri.AbsolutePath.StartsWith("/revit-model-mcp/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(helpUri.AbsolutePath).IsEqualTo("/revit-model-mcp/install/");
        await Assert.That(new Uri(HelpLinks.AddinHelpUrl).Fragment).IsEqualTo("#the-model-mcp-ribbon-tab");
    }
    [Test]
    [Arguments(HelpLinks.Documentation)]
    [Arguments(HelpLinks.AddinHelpUrl)]
    [Arguments(HelpLinks.Issues)]
    [Arguments(HelpLinks.Support)]
    [Arguments(HelpLinks.Source)]
    [Arguments(HelpLinks.Privacy)]
    public async Task HelpLinkIsAbsoluteHttps(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        await Assert.That(uri.IsAbsoluteUri).IsTrue();
        await Assert.That(uri.Scheme).IsEqualTo("https");
    }
}
