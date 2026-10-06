using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class ConfirmationStore
{
    private static readonly Dictionary<Document, (Guid SessionId, long ChangeCount)> Documents = [];

    internal static DocumentConfirmationTokens Tokens { get; } = new();

    internal static string Rejection(DocumentConfirmationResult result, string changedMessage) => result switch
    {
        DocumentConfirmationResult.DocumentChanged => changedMessage,
        DocumentConfirmationResult.ArgumentsMismatch =>
            "The arguments or models differ from the preview. The confirmation token is used up; repeat the call without confirm_token to get a new one, then confirm with identical arguments.",
        _ => "Confirmation token is unknown, expired or already used. Repeat the call without confirm_token to get a new one."
    };

    internal static ActionResultData? Gate(string command, string identity, string arguments, string state,
        string? token, string text, string summary, string changedMessage)
    {
        if (token is null)
            return new ActionResultData
            {
                NeedsConfirmation = true,
                ConfirmationText = text,
                ConfirmToken = Tokens.Issue(command, identity, arguments, state),
                Summary = summary
            };
        var result = Tokens.Consume(token, command, identity, arguments, state);
        if (result != DocumentConfirmationResult.Valid)
            throw new InvalidOperationException(Rejection(result, changedMessage));
        return null;
    }

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
