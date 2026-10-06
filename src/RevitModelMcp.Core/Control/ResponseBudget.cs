namespace RevitModelMcp.Core.Control;

public sealed record StoredResponse(string Key, DateTimeOffset CompletedUtc, long Bytes);

public static class ResponseBudget
{
    public static IReadOnlyList<string> SelectEvictions(IReadOnlyList<StoredResponse> entries, int maxCount, long maxBytes)
    {
        var ordered = entries.OrderBy(entry => entry.CompletedUtc).ToArray();
        var count = ordered.Length;
        var bytes = ordered.Sum(entry => entry.Bytes);
        var evictions = new List<string>();
        foreach (var entry in ordered.Take(Math.Max(0, ordered.Length - 1)))
        {
            if (count <= maxCount && bytes <= maxBytes) break;
            evictions.Add(entry.Key);
            count--;
            bytes -= entry.Bytes;
        }
        return evictions;
    }
}
