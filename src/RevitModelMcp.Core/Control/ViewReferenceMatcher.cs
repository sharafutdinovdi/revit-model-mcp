namespace RevitModelMcp.Core.Control;

public static class ViewReferenceMatcher
{
    public static T? Find<T>(
        IEnumerable<T> views,
        string reference,
        Func<T, long> idSelector,
        Func<T, string> nameSelector,
        Func<T, string?>? sheetNumberSelector = null,
        Func<T, string>? typeSelector = null)
        where T : class
    {
        var candidates = views as IReadOnlyList<T> ?? views.ToList();
        var byName = candidates.Where(view =>
            string.Equals(nameSelector(view), reference, StringComparison.Ordinal)).ToList();
        var bySheetNumber = sheetNumberSelector is null ? [] : candidates.Where(view =>
            string.Equals(sheetNumberSelector(view), reference, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = byName.Concat(bySheetNumber).Distinct().ToList();
        if (matches.Count > 1)
        {
            throw new InvalidOperationException($"View '{reference}' is ambiguous: " +
                string.Join("; ", matches.Select(view =>
                    $"id={idSelector(view)}, name={nameSelector(view)}, type={typeSelector?.Invoke(view) ?? "unknown"}")));
        }
        if (byName.Count == 1) return byName[0];

        if (long.TryParse(reference, out var id))
        {
            var byId = candidates.FirstOrDefault(view => idSelector(view) == id);
            if (byId is not null) return byId;
        }
        return bySheetNumber.SingleOrDefault();
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

        if (job.TargetProcessId.HasValue)
        {
            return true;
        }

        if (job.TargetDocument is null)
        {
            return true;
        }

        return MatchesDocument(documentTitle, documentPath, job.TargetDocument);
    }

    public static bool MatchesDocument(string? title, string? path, string reference)
    {
        var fileName = Path.GetFileName(path ?? string.Empty);
        return Contains(title, reference) || Contains(fileName, reference);
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
