using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Control;

public static class OwnedDocumentCleanup
{
    public static IEnumerable<T> FindUntrackedActivationDocuments<T>(IEnumerable<T> open,
        IEnumerable<T> preExisting, IEnumerable<T> owned, IEqualityComparer<T> comparer,
        IEnumerable<DocumentClosureResult> closures, Func<T, string> identity, Func<T, bool> matchesPath)
        where T : class
    {
        foreach (var candidate in open)
        {
            if (!preExisting.Contains(candidate, comparer) && !owned.Contains(candidate, comparer) &&
                !closures.Any(closure => closure.Identity == identity(candidate)) && matchesPath(candidate))
                yield return candidate;
        }
    }

    public static DocumentClosureResult? Close<T>(T document, IEnumerable<T> preExisting,
        IEqualityComparer<T> comparer, string identity, Func<T, bool> close) where T : class
    {
        if (preExisting.Contains(document, comparer)) return null;
        try
        {
            return new DocumentClosureResult { Identity = identity, Closed = close(document) };
        }
        catch (Exception exception)
        {
            return new DocumentClosureResult { Identity = identity, Error = exception.GetType().Name };
        }
    }
}

[DataContract]
public sealed class DocumentClosureResult
{
    [DataMember(Name = "identity")] public string Identity { get; set; } = string.Empty;
    [DataMember(Name = "closed")] public bool Closed { get; set; }
    [DataMember(Name = "error", EmitDefaultValue = false)] public string? Error { get; set; }

    public override string ToString() => Closed
        ? $"Closed '{Identity}'."
        : $"Retained document '{Identity}': closure {(Error is null ? "refused" : $"failed ({Error})")}.";
}
