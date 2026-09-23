using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Control;

internal static class FamilyPurge
{
#if REVIT2024_OR_GREATER
    internal const string Coverage = "full";
#else
    internal const string Coverage = "families-and-types";
    private static readonly PerformanceAdviserRuleId RuleId = new(new Guid("e8c63650-70b7-435a-9010-ec97660c1bda"));
#endif

    internal static List<ElementId> Candidates(Document document)
    {
#if REVIT2024_OR_GREATER
        return document.GetUnusedElements(new HashSet<ElementId>()).ToList();
#else
        var adviser = PerformanceAdviser.GetPerformanceAdviser();
        if (!adviser.GetAllRuleIds().Contains(RuleId))
            throw new InvalidOperationException("The unused families and types rule is unavailable.");
        return adviser.ExecuteRules(document, [RuleId])
            .SelectMany(failure => failure.GetFailingElements()).Distinct().ToList();
#endif
    }

    internal static Dictionary<string, int> Counts(Document document, IEnumerable<ElementId> ids) =>
        ids.Select(document.GetElement).Where(element => element is not null)
            .GroupBy(element => element!.Category?.Name ?? element.GetType().Name)
            .ToDictionary(group => group.Key, group => group.Count());

    internal static FamilyOperationResult Execute(Document document)
    {
        var result = new FamilyOperationResult { Op = "purge", Coverage = Coverage, Deleted = [] };
        for (var pass = 0; pass < 5; pass++)
        {
            var candidates = Candidates(document);
            if (candidates.Count == 0) break;
            foreach (var (category, count) in Counts(document, candidates))
                result.Deleted[category] = result.Deleted.GetValueOrDefault(category) + count;
            document.Delete(candidates);
            result.Passes = pass + 1;
        }
        result.Passes ??= 0;
        return result;
    }
}
