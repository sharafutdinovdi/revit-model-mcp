using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed record SkippedRead
{
    [DataMember(Name = "what")] public string What { get; init; } = string.Empty;
    [DataMember(Name = "reason")] public string Reason { get; init; } = string.Empty;
    [DataMember(Name = "dialogId", EmitDefaultValue = false)] public string? DialogId { get; init; }
    [DataMember(Name = "message", EmitDefaultValue = false)] public string? Message { get; init; }
}

public sealed class SkippedReadDiagnostics
{
    public const int Limit = 100;

    private static readonly AsyncLocal<SkippedReadDiagnostics?> Active = new();

    public static SkippedReadDiagnostics? Current
    {
        get => Active.Value;
        set => Active.Value = value;
    }

    public List<SkippedRead> Items { get; } = [];

    public int Count { get; private set; }

    public static SkippedReadDiagnostics ForResponse(object? data, IEnumerable<SkippedRead>? skipped = null, int count = 0)
    {
        var result = new SkippedReadDiagnostics();
        if (Current is not null) result.Merge(Current);
        switch (data)
        {
            case ModelSnapshotData snapshot:
                result.Merge(snapshot.Skipped, Current is null ? snapshot.SkippedCount : 0);
                result.Count = Math.Max(result.Count, snapshot.SkippedCount);
                break;
            case ApiLookupResult lookup:
                result.Merge(lookup.Skipped, lookup.SkippedCount);
                if (lookup.Truncated)
                    result.Merge([new SkippedRead
                    {
                        What = "api-lookup results",
                        Reason = "Matching entries omitted at the result limit."
                    }], 1);
                break;
            case FamilyAuditData audit:
                result.Merge(audit.Skipped, audit.SkippedCount);
                foreach (var family in audit.Families.Where(family => family.Status is "skipped" or "failed"))
                    result.Merge([new SkippedRead
                    {
                        What = $"family {family.Name} audit",
                        Reason = family.Reason ?? "Family inspection was omitted."
                    }], 1);
                break;
        }
        result.Merge(skipped ?? [], 0);
        result.Count = Math.Max(result.Count, count);
        return result;
    }

    private readonly HashSet<SkippedRead> merged = [];
    private int unlistedCount;

    public void Merge(SkippedReadDiagnostics diagnostics)
    {
        if (diagnostics is null) throw new ArgumentNullException(nameof(diagnostics));
        Merge(diagnostics.merged, diagnostics.Count);
    }

    public void Merge(IEnumerable<SkippedRead> items, int count)
    {
        var details = items.ToList();
        merged.UnionWith(Items);
        unlistedCount = Math.Max(unlistedCount, Math.Max(Count - merged.Count, count - details.Count));
        foreach (var item in details)
            if (merged.Add(item) && Items.Count < Limit) Items.Add(item);
        Count = merged.Count + unlistedCount;
    }

    public void AddOmitted(string what, int total, int retained)
    {
        if (total <= retained) return;
        Add(what, new InvalidOperationException($"{total - retained} of {total} entries omitted; list limit is {retained}."));
    }

    public void Add(string what, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(what)) throw new ArgumentException("A field description is required.", nameof(what));
        if (exception is null) throw new ArgumentNullException(nameof(exception));
        Merge([new SkippedRead { What = what, Reason = exception.Message }], 1);
    }
}
