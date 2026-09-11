using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class ModelWarningReader
{
    public static ModelWarningsData Read(Document document, string? warningText, bool includeElements)
    {
        var warnings = document.GetWarnings();
        var selected = warningText is null
            ? warnings
            : warnings.Where(warning => string.Equals(
                warning.GetDescriptionText(), warningText, StringComparison.OrdinalIgnoreCase)).ToList();
        if (warningText is not null && selected.Count == 0)
        {
            throw new ArgumentException(
                $"Предупреждение с текстом «{warningText}» не найдено. Сначала вызовите list-warnings без warningText.");
        }

        var groups = selected.GroupBy(
            warning => warning.GetDescriptionText(),
            StringComparer.OrdinalIgnoreCase);
        return new ModelWarningsData
        {
            TotalWarnings = selected.Count,
            Groups = groups.Select(group => CreateGroup(document, group.ToList(), includeElements))
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Text, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static ModelWarningGroup CreateGroup(
        Document document,
        IReadOnlyList<FailureMessage> warnings,
        bool includeElements)
    {
        var ids = warnings.SelectMany(warning => warning.GetFailingElements().Concat(warning.GetAdditionalElements()))
            .Distinct()
            .ToList();
        return new ModelWarningGroup
        {
            Text = warnings[0].GetDescriptionText(),
            Severity = warnings[0].GetSeverity().ToString(),
            Count = warnings.Count,
            AffectedElementCount = ids.Count,
            Elements = includeElements
                ? ids.Select(id => document.GetElement(id)).Where(element => element is not null)
                    .Select(element => new ModelWarningElement
                    {
                        Id = RevitValueReader.GetId(element!.Id),
                        Category = element.Category?.Name,
                        Name = element.Name
                    }).ToList()
                : null
        };
    }
}
