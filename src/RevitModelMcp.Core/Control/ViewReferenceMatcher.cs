namespace RevitModelMcp.Core.Control;

public static class ViewReferenceMatcher
{
    public static T? Find<T>(
        IEnumerable<T> views,
        string reference,
        Func<T, long> idSelector,
        Func<T, string> nameSelector)
        where T : class
    {
        var candidates = views as IReadOnlyList<T> ?? views.ToList();
        var byName = candidates.FirstOrDefault(view =>
            string.Equals(nameSelector(view), reference, StringComparison.Ordinal));
        if (byName is not null)
        {
            return byName;
        }

        return long.TryParse(reference, out var id)
            ? candidates.FirstOrDefault(view => idSelector(view) == id)
            : null;
    }
}

public static class JobTargetMatcher
{
    public static bool Matches(
        ControlJobParseResult job,
        string? documentTitle,
        string? documentPath,
        int processId)
    {
        if (job.TargetProcessId.HasValue && job.TargetProcessId.Value != processId)
        {
            return false;
        }

        if (job.TargetDocument is null)
        {
            return true;
        }

        var fileName = Path.GetFileName(documentPath ?? string.Empty);
        return Contains(documentTitle, job.TargetDocument) || Contains(fileName, job.TargetDocument);
    }

    public static bool TryClaim(
        string triggerPath,
        ControlJobParseResult job,
        string? documentTitle,
        string? documentPath,
        int processId)
    {
        if (!Matches(job, documentTitle, documentPath, processId))
        {
            return false;
        }

        File.Delete(triggerPath);
        return true;
    }

    private static bool Contains(string? value, string target)
    {
        return value?.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
