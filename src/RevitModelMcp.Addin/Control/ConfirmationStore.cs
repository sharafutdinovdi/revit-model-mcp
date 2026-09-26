using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class ConfirmationStore
{
    private static readonly Dictionary<Document, (Guid SessionId, long ChangeCount)> Documents = [];

    internal static DocumentConfirmationTokens Tokens { get; } = new();

    internal static string State(Document document)
    {
        if (!Documents.TryGetValue(document, out var tracked))
        {
            tracked = (Guid.NewGuid(), 0);
            Documents[document] = tracked;
        }
        var version = Document.GetDocumentVersion(document);
        return DocumentConfirmationBinding.State(version.VersionGUID, version.NumberOfSaves,
            tracked.SessionId, tracked.ChangeCount);
    }

    internal static void DocumentChanged(Document document)
    {
        if (!Documents.TryGetValue(document, out var tracked)) tracked = (Guid.NewGuid(), 0);
        Documents[document] = (tracked.SessionId, checked(tracked.ChangeCount + 1));
    }

    internal static void DocumentClosing(Document document) => Documents.Remove(document);
}
