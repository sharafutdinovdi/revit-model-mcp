namespace RevitModelMcp.Core.Help;

/// <summary>
///     Provides help links for the add-in.
/// </summary>
public static class HelpLinks
{
    /// <summary>
    ///     The help URL for the MCP ribbon panel.
    /// </summary>
#if STORE_CHANNEL
    public const string AddinHelpUrl = "https://github.com/sharafutdinovdi/revit-model-mcp";
#else
    public const string AddinHelpUrl = "https://sharafutdinovdi.github.io/revit-model-mcp/install/#the-mcp-ribbon-panel";
#endif
}
