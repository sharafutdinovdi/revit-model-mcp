namespace RevitModelMcp.Core.Control;

public static class ProcessOutputCollisions
{
    private const int MaxGroups = 5;

    public static IReadOnlyList<IReadOnlyList<string>> Find(IEnumerable<string> sources)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var source in sources)
        {
            var name = ModelName(source);
            if (!groups.TryGetValue(name, out var group))
            {
                group = [];
                groups[name] = group;
                order.Add(name);
            }
            group.Add(source);
        }
        return order.Where(name => groups[name].Count >= 2).Select(name => (IReadOnlyList<string>)groups[name]).ToList();
    }

    public static string FormatError(IReadOnlyList<IReadOnlyList<string>> groups)
    {
        var listed = groups.Take(MaxGroups)
            .Select(group => $"\"{ModelName(group[0])}\" ({string.Join(", ", group)})");
        var text = string.Join("; ", listed);
        if (groups.Count > MaxGroups) text += $", and {groups.Count - MaxGroups} more";
        return "Sources share a model name, so their outputs would overwrite each other: " + text +
            ". Process them in separate calls or with different output folders.";
    }

    private static string ModelName(string source)
    {
        var normalized = source.Replace('/', '\\');
        var file = normalized.Substring(normalized.LastIndexOf('\\') + 1);
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file.Substring(0, dot) : file;
    }
}
