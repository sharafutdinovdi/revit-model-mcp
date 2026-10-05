namespace RevitModelMcp.Core.Formatting;

public static class RepeatedMessages
{
    public static string Join(IEnumerable<string> messages)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var message in messages)
        {
            if (counts.TryGetValue(message, out var count)) counts[message] = count + 1;
            else
            {
                counts[message] = 1;
                order.Add(message);
            }
        }
        return string.Join("; ", order.Select(message => counts[message] > 1 ? $"{message} (x{counts[message]})" : message));
    }
}
