namespace RevitModelMcp.Core.Control;

public static class ProcessModelsResultAssembler
{
    public static void AppendCancelled(List<ProcessModelResult> models, IReadOnlyList<string> paths, int firstUnprocessedIndex)
    {
        if (models is null) throw new ArgumentNullException(nameof(models));
        if (paths is null) throw new ArgumentNullException(nameof(paths));

        for (var index = Math.Max(0, firstUnprocessedIndex); index < paths.Count; index++)
        {
            models.Add(new ProcessModelResult { Path = paths[index], Status = "cancelled", ElapsedMs = 0 });
        }
    }
}
