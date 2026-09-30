using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal sealed partial class SnapshotCollector
{
    private static readonly Regex DuplicateNameSuffixRegex = new(
        @" \(\d+\)$",
        RegexOptions.CultureInvariant);

    private void CaptureModelCounts()
    {
        if (_document is null)
        {
            return;
        }

        TryCapture("snapshot model counts total regions", () => _snapshot.ModelCounts.TotalRegions = CountAllRegions());
        TryCapture("snapshot model counts coordination views", () => _snapshot.ModelCounts.CoordinationViews = CountViewsWithPrefix("Coordination_"));
        TryCapture("snapshot model counts construction views", () => _snapshot.ModelCounts.ConstructionViews = CountViewsWithPrefix("Construction_"));
        TryCapture("snapshot model counts sheets", () => _snapshot.ModelCounts.Sheets = new FilteredElementCollector(_document)
            .OfClass(typeof(ViewSheet))
            .WhereElementIsNotElementType()
            .GetElementCount());
        TryCapture("snapshot model counts section views", () => _snapshot.ModelCounts.SectionViews = new FilteredElementCollector(_document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Count(view => !view.IsTemplate && view.ViewType == ViewType.Section));
    }

    private int CountAllRegions()
    {
        var count = 0;
        var collector = new FilteredElementCollector(_document!)
            .WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance));

        foreach (var element in collector)
        {
            try
            {
                if (IsRegion(RevitValueReader.GetFamilyName(element)))
                {
                    count++;
                }
            }
            catch (Exception exception)
            {
                PluginLog.Skipped($"snapshot model counts region element {RevitValueReader.GetId(element.Id)}", exception);
            }
        }

        return count;
    }

    private int CountViewsWithPrefix(string prefix)
    {
        return new FilteredElementCollector(_document!)
            .OfClass(typeof(View))
            .Cast<View>()
            .Count(view => !view.IsTemplate && view.Name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private void CaptureSectionViewInventory()
    {
        if (_document is null)
        {
            return;
        }

        var sections = new FilteredElementCollector(_document)
            .OfClass(typeof(ViewSection))
            .Cast<ViewSection>();

        foreach (var section in sections)
        {
            var target = new SectionViewSnapshot();
            TryCapture("snapshot section view inventory id", () => target.Id = RevitValueReader.GetId(section.Id));
            TryCapture($"snapshot section view {target.Id} name", () => target.Name = section.Name);
            TryCapture($"snapshot section view {target.Id} template", () => target.TemplateName = GetTemplateName(section));
            TryCapture($"snapshot section view {target.Id} scale", () => target.Scale = section.Scale);
            TryCapture($"snapshot section view {target.Id} crop active", () => target.CropActive = section.CropBoxActive);
            TryCapture($"snapshot section view {target.Id} crop bounds", () =>
            {
                var cropBox = section.CropBox;
                target.CropBboxMm = cropBox is null
                    ? null
                    : RevitValueReader.ToSectionCropBoundingBox(cropBox);
            });
            target.IsNameDuplicate = IsDuplicateName(target.Name);
            target.Prefix = GetViewPrefix(target.Name);
            _snapshot.AllSectionViews.Add(target);
        }

        _snapshot.AllSectionViews = _snapshot.AllSectionViews
            .OrderBy(section => section.Id)
            .ToList();
    }

    private void CaptureDuplicateBaseNames()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var views = new FilteredElementCollector(_document!)
            .OfClass(typeof(View))
            .Cast<View>();

        foreach (var view in views)
        {
            try
            {
                var name = view.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var baseName = DuplicateNameSuffixRegex.Replace(name, string.Empty);
                counts[baseName] = counts.TryGetValue(baseName, out var count) ? count + 1 : 1;
            }
            catch (Exception exception)
            {
                PluginLog.Skipped($"snapshot duplicate base names view {RevitValueReader.GetId(view.Id)}", exception);
            }
        }

        _snapshot.DuplicateBaseNames = counts
            .Where(pair => pair.Value > 1)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DuplicateBaseNameSnapshot
            {
                BaseName = pair.Key,
                Count = pair.Value
            })
            .ToList();
    }

    private string? GetTemplateName(View view)
    {
        var templateId = view.ViewTemplateId;
        return RevitValueReader.IsValidId(templateId)
            ? _document!.GetElement(templateId)?.Name
            : null;
    }

    private static bool IsDuplicateName(string? name)
    {
        return name is not null && DuplicateNameSuffixRegex.IsMatch(name);
    }

    private static string GetViewPrefix(string? name)
    {
        if (name?.StartsWith("Coordination", StringComparison.Ordinal) == true)
        {
            return "Coordination";
        }

        return name?.StartsWith("Construction", StringComparison.Ordinal) == true ? "Construction" : "other";
    }
}
