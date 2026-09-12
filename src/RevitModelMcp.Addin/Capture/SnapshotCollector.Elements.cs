using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal sealed partial class SnapshotCollector
{
    private const int AnnotationLimit = 200;
    private const int CurtainPanelLimit = 400;

    private static readonly string[] AnnotationParameterNames =
    {
        "Segment",
        "Number",
        "Zone Code"
    };

    private static readonly string[] CurtainPanelParameterNames =
    {
        "Segment",
        "Number",
        "Zone Code",
        "NameOverride"
    };

    private void CaptureRegions()
    {
        if (_document is null || _view is null)
        {
            return;
        }

        var collector = new FilteredElementCollector(_document, _view.Id)
            .WhereElementIsNotElementType()
            .OfClass(typeof(FamilyInstance));

        foreach (var element in collector)
        {
            try
            {
                var familyName = RevitValueReader.GetFamilyName(element);
                if (!IsRegion(familyName))
                {
                    continue;
                }

                _snapshot.Regions.Add(new RegionSnapshot
                {
                    Id = RevitValueReader.GetId(element.Id),
                    FamilyName = familyName,
                    TypeName = RevitValueReader.GetTypeName(_document, element),
                    Mark = RevitValueReader.GetMark(element),
                    Comment1 = RevitValueReader.GetNamedParameter(element, "Comment 1"),
                    Comment2 = RevitValueReader.GetNamedParameter(element, "Comment 2"),
                    OwnerViewName = GetOwnerViewName(element),
                    BboxOnViewMm = RevitValueReader.GetBoundingBoxOnView(element, _view)
                });
            }
            catch
            {
                // One malformed family instance must not hide the remaining regions.
            }
        }

        _snapshot.Regions = _snapshot.Regions.OrderBy(region => region.Id).ToList();
        _snapshot.RegionCount = _snapshot.Regions.Count;
    }

    private void CaptureElementsOnView()
    {
        if (_document is null || _view is null)
        {
            return;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var collector = new FilteredElementCollector(_document, _view.Id)
            .WhereElementIsNotElementType();

        foreach (var element in collector)
        {
            try
            {
                var categoryName = element.Category?.Name ?? "<no category>";
                counts[categoryName] = counts.TryGetValue(categoryName, out var count) ? count + 1 : 1;
            }
            catch
            {
                // Continue counting categories that remain readable.
            }
        }

        _snapshot.ElementsOnView = counts
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private void CaptureAnnotations()
    {
        if (_document is null || _view is null)
        {
            return;
        }

        var categories = new[]
        {
            BuiltInCategory.OST_DetailComponents,
            BuiltInCategory.OST_FilledRegion,
            BuiltInCategory.OST_GenericAnnotation,
            BuiltInCategory.OST_GenericModel
        };
        var filter = new ElementMulticategoryFilter(categories);
        var collector = new FilteredElementCollector(_document, _view.Id)
            .WhereElementIsNotElementType()
            .WherePasses(filter);

        foreach (var element in collector)
        {
            if (_snapshot.Annotations.Count >= AnnotationLimit)
            {
                break;
            }

            try
            {
                _snapshot.Annotations.Add(new AnnotationSnapshot
                {
                    Category = element.Category?.Name,
                    FamilyName = RevitValueReader.GetFamilyName(element),
                    TypeName = RevitValueReader.GetTypeName(_document, element),
                    Id = RevitValueReader.GetId(element.Id),
                    Params = ReadAnnotationParameters(element)
                });
            }
            catch
            {
                // Continue with the next visible annotation.
            }
        }
    }

    private void CaptureSelection()
    {
        if (_document is null || _view is null || _uiDocument is null)
        {
            return;
        }

        foreach (var id in _uiDocument.Selection.GetElementIds().OrderBy(RevitValueReader.GetId))
        {
            try
            {
                var element = _document.GetElement(id);
                if (element is null)
                {
                    continue;
                }

                _snapshot.Selection.Add(new SelectionSnapshot
                {
                    Category = element.Category?.Name,
                    FamilyName = RevitValueReader.GetFamilyName(element),
                    TypeName = RevitValueReader.GetTypeName(_document, element),
                    Id = RevitValueReader.GetId(element.Id),
                    Mark = RevitValueReader.GetMark(element),
                    BboxOnViewMm = RevitValueReader.GetBoundingBoxOnView(element, _view)
                });
            }
            catch
            {
                // Stale selection ids are ignored without aborting the snapshot.
            }
        }
    }

    private void CaptureCurtainPanels()
    {
        if (_document is null || _view is null)
        {
            return;
        }

        var panels = new FilteredElementCollector(_document, _view.Id)
            .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
            .WhereElementIsNotElementType()
            .ToElements();
        var stats = _snapshot.PanelStats;
        stats.PanelsOnView = panels.Count;
        _snapshot.CurtainPanelsCapped = panels.Count > CurtainPanelLimit;

        foreach (var element in panels)
        {
            try
            {
                var parameters = ReadCurtainPanelParameters(element);
                stats.PanelsWithSegment += parameters.ContainsKey("Segment") ? 1 : 0;
                stats.PanelsWithNumber += parameters.ContainsKey("Number") ? 1 : 0;
                stats.PanelsWithMark += parameters.ContainsKey("Mark") ? 1 : 0;

                if (_snapshot.CurtainPanels.Count < CurtainPanelLimit)
                {
                    _snapshot.CurtainPanels.Add(new CurtainPanelSnapshot
                    {
                        Id = RevitValueReader.GetId(element.Id),
                        FamilyName = RevitValueReader.GetFamilyName(element),
                        TypeName = RevitValueReader.GetTypeName(_document, element),
                        Params = parameters
                    });
                }
            }
            catch
            {
                // One malformed panel must not hide the remaining visible panels.
            }
        }

        _snapshot.CurtainPanels = _snapshot.CurtainPanels.OrderBy(panel => panel.Id).ToList();
        stats.PanelTagsOnView = new FilteredElementCollector(_document, _view.Id)
            .OfCategory(BuiltInCategory.OST_CurtainWallPanelTags)
            .WhereElementIsNotElementType()
            .GetElementCount();
    }

    private Dictionary<string, string> ReadAnnotationParameters(Element element)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(values, "Mark", RevitValueReader.GetMark(element));

        foreach (var parameterName in AnnotationParameterNames)
        {
            AddIfPresent(values, parameterName, RevitValueReader.GetNamedParameter(element, parameterName));
        }

        return values;
    }

    private Dictionary<string, string> ReadCurtainPanelParameters(Element element)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(values, "Mark", RevitValueReader.GetMark(element));

        foreach (var parameterName in CurtainPanelParameterNames)
        {
            AddIfPresent(values, parameterName, RevitValueReader.GetNamedParameter(element, parameterName));
        }

        return values;
    }

    private string? GetOwnerViewName(Element element)
    {
        var ownerViewId = element.OwnerViewId;
        return RevitValueReader.IsValidId(ownerViewId)
            ? _document!.GetElement(ownerViewId)?.Name
            : null;
    }

    private static bool IsRegion(string? familyName)
    {
        return familyName?.IndexOf("Region", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void AddIfPresent(Dictionary<string, string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[name] = value!;
        }
    }
}
