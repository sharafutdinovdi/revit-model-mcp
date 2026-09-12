using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Units;

namespace RevitModelMcp.Capture;

internal static class ReadCommandReader
{
    public static ResponderInfo ReadResponder(UIApplication application)
    {
        var document = application.ActiveUIDocument?.Document;
        return new ResponderInfo
        {
            DocumentName = document?.Title ?? string.Empty,
            DocumentPath = document?.PathName ?? string.Empty,
            ProcessId = Process.GetCurrentProcess().Id,
            RevitVersion = application.Application?.VersionNumber ?? string.Empty
        };
    }

    public static DocumentInfoData ReadDocumentInfo(UIApplication application)
    {
        var uiDocument = application.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        var document = uiDocument.Document;
        var roomCounts = new Dictionary<long, int>();
        foreach (var room in new FilteredElementCollector(document)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType()
                     .OfType<Room>())
        {
            Increment(roomCounts, room.LevelId);
        }

        var areaCounts = new Dictionary<long, int>();
        foreach (var area in new FilteredElementCollector(document)
                     .OfCategory(BuiltInCategory.OST_Areas)
                     .WhereElementIsNotElementType()
                     .OfType<Area>())
        {
            Increment(areaCounts, area.AreaScheme?.Id);
        }

        var levels = new FilteredElementCollector(document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(level => level.Elevation)
            .Select(level => new DocumentLevelInfo
            {
                Name = level.Name,
                ElevationMm = UnitConverter.FeetToMillimeters(level.Elevation),
                RoomCount = roomCounts.GetValueOrDefault(RevitValueReader.GetId(level.Id))
            })
            .ToList();
        var schemes = new FilteredElementCollector(document)
            .OfClass(typeof(AreaScheme))
            .Cast<AreaScheme>()
            .OrderBy(scheme => scheme.Name, StringComparer.Ordinal)
            .Select(scheme => new DocumentAreaSchemeInfo
            {
                Name = scheme.Name,
                IsGrossBuildingArea = scheme.IsGrossBuildingArea,
                AreaCount = areaCounts.GetValueOrDefault(RevitValueReader.GetId(scheme.Id))
            })
            .ToList();
        var worksets = document.IsWorkshared
            ? new FilteredWorksetCollector(document)
                .OfKind(WorksetKind.UserWorkset)
                .ToWorksets()
                .OrderBy(workset => workset.Name, StringComparer.Ordinal)
                .Select(workset => new DocumentWorksetInfo
                {
                    Name = workset.Name,
                    Kind = workset.Kind.ToString(),
                    IsOpen = workset.IsOpen
                })
                .ToList()
            : new List<DocumentWorksetInfo>();
        var viewCount = new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Count(view => !view.IsTemplate);

        return new DocumentInfoData
        {
            FileName = string.IsNullOrWhiteSpace(document.PathName)
                ? document.Title
                : Path.GetFileName(document.PathName),
            RevitVersion = application.Application.VersionNumber,
            IsWorkshared = document.IsWorkshared,
            Levels = levels,
            AreaSchemes = schemes,
            Worksets = worksets,
            ViewCount = viewCount
        };
    }

    public static ViewListData ReadViews(
        Document document,
        string? viewType,
        string? nameContains,
        Func<ViewListData, string, long, bool>? onProgress = null)
    {
        var result = new ViewListData();
        var stopwatch = Stopwatch.StartNew();
        if (onProgress?.Invoke(result, "<collector-start>", stopwatch.ElapsedMilliseconds) == false)
        {
            return result;
        }

        var views = new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => !view.IsTemplate)
            .ToList();
        result.Total = views.Count;
        for (var index = 0; index < views.Count; index++)
        {
            var view = views[index];
            var name = view.Name;
            if (onProgress?.Invoke(result, name, stopwatch.ElapsedMilliseconds) == false)
            {
                break;
            }

            var type = view.ViewType.ToString();
            if (viewType is not null && !string.Equals(type, viewType, StringComparison.OrdinalIgnoreCase))
            {
                result.Processed = index + 1;
                continue;
            }

            if (nameContains is not null && name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                result.Processed = index + 1;
                continue;
            }

            result.Views.Add(new ViewListItem
            {
                Id = RevitValueReader.GetId(view.Id),
                Name = name,
                Type = type,
                Level = (view as ViewPlan)?.GenLevel?.Name,
                Scale = view.Scale,
                Template = ReadTemplateName(document, view)
            });
            result.Processed = index + 1;
        }

        result.Views = result.Views
            .OrderBy(view => view.Type, StringComparer.Ordinal)
            .ThenBy(view => view.Name, StringComparer.Ordinal)
            .ToList();
        onProgress?.Invoke(result, string.Empty, stopwatch.ElapsedMilliseconds);
        return result;
    }

    public static ViewSummaryData ReadViewSummary(Document document, View view)
    {
        var categories = new Dictionary<string, CategoryAccumulator>(StringComparer.Ordinal);
        var count = 0;
        foreach (var element in new FilteredElementCollector(document, view.Id)
                     .WhereElementIsNotElementType())
        {
            count++;
            var name = element.Category?.Name ?? "<no category>";
            if (!categories.TryGetValue(name, out var category))
            {
                category = new CategoryAccumulator(name);
                categories[name] = category;
            }

            category.Count++;
            var typeId = element.GetTypeId();
            if (RevitValueReader.IsValidId(typeId))
            {
                category.TypeIds.Add(RevitValueReader.GetId(typeId));
            }
        }

        return new ViewSummaryData
        {
            Header = ReadHeader(document, view, count),
            Categories = categories.Values
                .OrderByDescending(category => category.Count)
                .ThenBy(category => category.Name, StringComparer.Ordinal)
                .Select(category => new ViewCategorySummary
                {
                    Category = category.Name,
                    Count = category.Count,
                    DifferentTypes = category.TypeIds.Count
                })
                .ToList()
        };
    }

    public static ViewWarningsData ReadViewWarnings(Document document, View view)
    {
        var viewIds = new FilteredElementCollector(document, view.Id)
            .WhereElementIsNotElementType()
            .ToElementIds()
            .Select(RevitValueReader.GetId)
            .ToHashSet();
        var warnings = document.GetWarnings()
            .Select(warning =>
            {
                var elements = warning.GetFailingElements()
                    .Concat(warning.GetAdditionalElements())
                    .Select(RevitValueReader.GetId)
                    .Distinct()
                    .Select(id => new ViewWarningElementInfo
                    {
                        Id = id,
                        PresentOnView = viewIds.Contains(id)
                    })
                    .ToList();
                return new ViewWarningInfo
                {
                    Text = warning.GetDescriptionText(),
                    Severity = warning.GetSeverity().ToString(),
                    HasElementsOnView = elements.Any(element => element.PresentOnView),
                    Elements = elements
                };
            })
            .ToList();

        return new ViewWarningsData
        {
            View = view.Name,
            MatchingNote = "Document warnings are filtered by involved elements present in the view.",
            Warnings = warnings.Where(warning => warning.HasElementsOnView).ToList()
        };
    }

    public static View? FindView(Document document, string name)
    {
        var views = new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => !view.IsTemplate);
        return ViewReferenceMatcher.Find(views, name, view => RevitValueReader.GetId(view.Id), view => view.Name);
    }

    public static ViewDumpHeader ReadHeader(Document document, View view, int elementCount)
    {
        var filterIds = view.GetFilters();
        return new ViewDumpHeader
        {
            Name = view.Name,
            Type = view.ViewType.ToString(),
            Level = (view as ViewPlan)?.GenLevel?.Name,
            Scale = view.Scale,
            Template = ReadTemplateName(document, view),
            Discipline = RevitValueReader.GetParameterText(
                view.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE)),
            FilterCount = filterIds.Count,
            GraphicOverrideCount = filterIds.Count(filterId => HasGraphicOverrides(view.GetFilterOverrides(filterId))),
            ElementCount = elementCount
        };
    }

    public static HashSet<long> ReadWarningElementIds(Document document)
    {
        return document.GetWarnings()
            .SelectMany(warning => warning.GetFailingElements().Concat(warning.GetAdditionalElements()))
            .Select(RevitValueReader.GetId)
            .ToHashSet();
    }

    private static void Increment(Dictionary<long, int> counts, ElementId? id)
    {
        if (!RevitValueReader.IsValidId(id))
        {
            return;
        }

        var value = RevitValueReader.GetId(id!);
        counts[value] = counts.GetValueOrDefault(value) + 1;
    }

    private static string? ReadTemplateName(Document document, View view)
    {
        return RevitValueReader.IsValidId(view.ViewTemplateId)
            ? document.GetElement(view.ViewTemplateId)?.Name
            : null;
    }

    private static bool HasGraphicOverrides(OverrideGraphicSettings settings)
    {
        return settings.Halftone ||
               settings.Transparency > 0 ||
               settings.ProjectionLineWeight >= 0 ||
               settings.CutLineWeight >= 0 ||
               settings.ProjectionLineColor.IsValid ||
               settings.CutLineColor.IsValid ||
               RevitValueReader.IsValidId(settings.ProjectionLinePatternId) ||
               RevitValueReader.IsValidId(settings.CutLinePatternId) ||
               RevitValueReader.IsValidId(settings.SurfaceForegroundPatternId) ||
               RevitValueReader.IsValidId(settings.CutForegroundPatternId);
    }

    private sealed class CategoryAccumulator
    {
        public CategoryAccumulator(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int Count { get; set; }

        public HashSet<long> TypeIds { get; } = new();
    }
}
