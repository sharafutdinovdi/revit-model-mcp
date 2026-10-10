namespace RevitModelMcp.Core.Control;

/// <summary>Drops copies that Revit reported at creation but removed again at commit (for example unhostable doors).</summary>
public static class CopyResultReconciler
{
    public static (List<List<long>> Copies, int Dropped) Reconcile(IReadOnlyList<List<long>> copies, Func<long, bool> exists)
    {
        var kept = new List<List<long>>(copies.Count);
        var dropped = 0;
        foreach (var group in copies)
        {
            var surviving = new List<long>(group.Count);
            foreach (var id in group)
            {
                if (exists(id)) surviving.Add(id);
                else dropped++;
            }
            kept.Add(surviving);
        }
        return (kept, dropped);
    }

    public static string Warning(int dropped) =>
        $"{dropped} {(dropped == 1 ? "copy was" : "copies were")} removed by Revit at commit (not hostable without the host element).";

    /// <summary>Copies of a rolled-back action: every group stays in place but no id survives, because none was kept.</summary>
    public static List<List<long>> EmptyGroups(IReadOnlyList<List<long>> copies) =>
        copies.Select(_ => new List<long>()).ToList();

    /// <summary>Summary for a copy or mirror whose copies were all removed or rolled back by Revit.</summary>
    public static string NothingCreatedSummary(string command, string documentTitle) =>
        $"{(command == "mirror" ? "Mirror" : "Copy")} created nothing in {documentTitle}: Revit removed every copy.";
}
