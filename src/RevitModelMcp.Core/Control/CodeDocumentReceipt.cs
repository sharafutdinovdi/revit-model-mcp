using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Control;

[DataContract]
public sealed record CodeDocumentReceipt
{
    [DataMember(Name = "document")] public string Document { get; init; } = string.Empty;
    [DataMember(Name = "added")] public int Added { get; set; }
    [DataMember(Name = "modified")] public int Modified { get; set; }
    [DataMember(Name = "deleted")] public int Deleted { get; set; }
    [DataMember(Name = "outcome")] public string Outcome { get; set; } = "unknown";
    [DataMember(Name = "warning", EmitDefaultValue = false)] public string? Warning { get; set; }

    public static void ApplyOutcome(ActionResultData result, IReadOnlyCollection<CodeDocumentReceipt> receipts)
    {
        result.Committed = receipts.Count > 0 && receipts.All(receipt => receipt.Outcome == "committed");
        result.RolledBack = receipts.Count > 0 && receipts.All(receipt => receipt.Outcome == "rolledBack");
        if (receipts.Any(receipt => receipt.Outcome == "unknown"))
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
