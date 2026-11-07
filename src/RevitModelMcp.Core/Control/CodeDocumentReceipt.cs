using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Control;

[DataContract]
public sealed record CodeDocumentReceipt
{
    [DataMember(Name = "document")] public string Document { get; init; } = string.Empty;
    [DataMember(Name = "documentKey")] public string DocumentKey { get; init; } = string.Empty;
    [DataMember(Name = "openedByCall")] public bool OpenedByCall { get; init; }
    [DataMember(Name = "undoName", EmitDefaultValue = false)] public string? UndoName { get; set; }
    [DataMember(Name = "added")] public int Added { get; set; }
    [DataMember(Name = "modified")] public int Modified { get; set; }
    [DataMember(Name = "deleted")] public int Deleted { get; set; }
    [DataMember(Name = "outcome")] public string Outcome { get; set; } = "unknown";
    [DataMember(Name = "warning", EmitDefaultValue = false)] public string? Warning { get; set; }
    [IgnoreDataMember] public bool HasChanges => Added > 0 || Modified > 0 || Deleted > 0;

    public static bool ShouldProtect(bool isLinked, bool isReadOnly, bool isModifiable) =>
        !isLinked && !isReadOnly && !isModifiable;

    public static bool RefusesUnprotectedDocument(string transactionMode, bool isLinked, bool isReadOnly, bool protectionStarted) =>
        transactionMode is "auto" or "group" && !isLinked && !isReadOnly && !protectionStarted;

    public static string GroupOutcome(bool rollbackRequested, bool completionConfirmed, bool hasChanges) =>
        !completionConfirmed ? "unknown" : rollbackRequested || !hasChanges ? "rolledBack" : "committed";

    public static void ApplyOutcome(ActionResultData result, IReadOnlyCollection<CodeDocumentReceipt> receipts)
    {
        var relevant = receipts.Where(receipt => receipt.HasChanges || receipt.OpenedByCall || receipt.Outcome == "unknown").ToList();
        if (relevant.Count == 0) relevant.AddRange(receipts);
        result.Committed = relevant.Count > 0 && relevant.All(receipt => receipt.Outcome is "committed" or "leftOpen" or "closed");
        result.RolledBack = relevant.Count > 0 && relevant.All(receipt => receipt.Outcome is "rolledBack" or "unchanged");
        if (relevant.Any(receipt => receipt.Outcome == "unknown"))
        {
            const string warning = "Some document changes have an unknown outcome and could not be confirmed rolled back. Changes may remain; inspect documentReceipts.";
            result.Warning = string.IsNullOrEmpty(result.Warning) ? warning : result.Warning + " " + warning;
            result.Summary = "Code execution completed with unconfirmed document changes. Changes may remain; inspect documentReceipts.";
        }
        else if (receipts.Count > 0 && result.Committed != true && result.RolledBack != true)
        {
            result.Warning = "Document outcomes differ. Committed changes remain; inspect documentReceipts.";
            result.Summary = "Code execution has mixed document outcomes. Committed changes remain; inspect documentReceipts.";
        }
    }
}
