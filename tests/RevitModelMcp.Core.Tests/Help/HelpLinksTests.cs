using RevitModelMcp.Core.Help;

namespace RevitModelMcp.Core.Tests.Help;

public sealed class HelpLinksTests
{
    [Test]
    public async Task AddinHelpUrlPointsToChannelHelp()
    {
        var helpUri = new Uri(HelpLinks.AddinHelpUrl, UriKind.Absolute);
        await Assert.That(helpUri.IsAbsoluteUri).IsTrue();
        await Assert.That(helpUri.Scheme).IsEqualTo("https");
#if STORE_CHANNEL
        await Assert.That(helpUri.Host).IsEqualTo("github.com");
        await Assert.That(helpUri.AbsolutePath).IsEqualTo("/sharafutdinovdi/revit-model-mcp");
        await Assert.That(helpUri.Fragment).IsEqualTo(string.Empty);
#else
        await Assert.That(helpUri.Host).IsEqualTo("sharafutdinovdi.github.io");
        await Assert.That(helpUri.AbsolutePath.StartsWith("/revit-model-mcp/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(helpUri.Fragment).IsEqualTo("#the-mcp-ribbon-panel");
#endif
    }
}
