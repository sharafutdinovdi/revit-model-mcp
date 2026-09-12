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

        TryCapture(() => _snapshot.ModelCounts.TotalRegions = CountAllRegions());
        TryCapture(() => _snapshot.ModelCounts.CoordinationViews = CountViewsWithPrefix("Coordination_"));
        TryCapture(() => _snapshot.ModelCounts.ConstructionViews = CountViewsWithPrefix("Construction_"));
        TryCapture(() => _snapshot.ModelCounts.Sheets = new FilteredElementCollector(_document)
            .OfClass(typeof(ViewSheet))
            .WhereElementIsNotElementType()
            .GetElementCount());
        TryCapture(() => _snapshot.ModelCounts.SectionViews = new FilteredElementCollector(_document)
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
            catch
            {
                // Count every readable family instance and skip only the broken one.
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
            TryCapture(() => target.Id = RevitValueReader.GetId(section.Id));
            TryCapture(() => target.Name = section.Name);
            TryCapture(() => target.TemplateName = GetTemplateName(section));
            TryCapture(() => target.Scale = section.Scale);
            TryCapture(() => target.CropActive = section.CropBoxActive);
            TryCapture(() =>
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
            catch
            {
                // One unreadable view must not hide duplicate names from the rest of the model.
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
