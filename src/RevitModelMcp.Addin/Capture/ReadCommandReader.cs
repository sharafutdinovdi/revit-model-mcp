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
    private static Dictionary<Document, string> DocumentIds =
        new(new DocumentIdentityComparer<Document>((first, second) => first.Equals(second), document => document.IsValidObject));

    internal static string DocumentId(Document document)
    {
        if (DocumentIds.TryGetValue(document, out var identity)) return identity;
        identity = "doc:" + Guid.NewGuid().ToString("N");
        DocumentIds.Add(document, identity);
        return identity;
    }

    internal static void PruneDocumentIds()
    {
        if (DocumentIds.Keys.All(document => document.IsValidObject)) return;
        var open = new Dictionary<Document, string>(DocumentIds.Comparer);
        foreach (var entry in DocumentIds)
        {
            if (entry.Key.IsValidObject) open.Add(entry.Key, entry.Value);
        }
        DocumentIds = open;
    }

    public static ResponderInfo ReadResponder(UIApplication application, Document? document = null)
    {
        var active = application.ActiveUIDocument?.Document;
        return new ResponderInfo
        {
            DocumentName = document?.Title ?? string.Empty,
            DocumentPath = document?.PathName ?? string.Empty,
            DocumentId = document is null ? null : DocumentId(document),
            ActiveDocument = active is null ? null : new InstanceDocument
            {
                Title = active.Title,
                Path = active.PathName,
                DocumentId = DocumentId(active),
                IsActive = true,
                IsFamilyDocument = active.IsFamilyDocument
            },
            InstanceId = RevitModelMcp.Application.InstanceId,
            ProcessId = Process.GetCurrentProcess().Id,
            RevitVersion = application.Application?.VersionNumber ?? string.Empty
        };
    }

    public static DocumentInfoData ReadDocumentInfo(UIApplication application)
    {
        var uiDocument = application.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
        return ReadDocumentInfo(uiDocument.Document);
    }

    public static IReadOnlyList<Workset> ReadUserWorksets(Document document)
    {
        if (!document.IsWorkshared) return [];
        using var collector = new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset);
        return collector.ToWorksets().OrderBy(workset => workset.Name, StringComparer.Ordinal).ToList();
    }

    public static DocumentInfoData ReadDocumentInfo(Document document)
    {
        var roomCounts = new Dictionary<long, int>();
        using var rooms = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType();
        foreach (var room in rooms.OfType<Room>())
        {
            Increment(roomCounts, room.LevelId);
        }

        var areaCounts = new Dictionary<long, int>();
        using var areas = new FilteredElementCollector(document)
            .OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType();
        foreach (var area in areas.OfType<Area>())
        {
            Increment(areaCounts, area.AreaScheme?.Id);
        }

        using var levelCollector = new FilteredElementCollector(document).OfClass(typeof(Level));
        var levels = levelCollector.Cast<Level>()
            .OrderBy(level => level.Elevation)
            .Select(level => new DocumentLevelInfo
            {
                Name = level.Name,
                ElevationMm = UnitConverter.FeetToMillimeters(level.Elevation),
                RoomCount = roomCounts.GetValueOrDefault(RevitValueReader.GetId(level.Id))
            })
            .ToList();
        using var schemeCollector = new FilteredElementCollector(document).OfClass(typeof(AreaScheme));
        var schemes = schemeCollector.Cast<AreaScheme>()
            .OrderBy(scheme => scheme.Name, StringComparer.Ordinal)
            .Select(scheme => new DocumentAreaSchemeInfo
            {
                Name = scheme.Name,
                IsGrossBuildingArea = scheme.IsGrossBuildingArea,
                AreaCount = areaCounts.GetValueOrDefault(RevitValueReader.GetId(scheme.Id))
            })
            .ToList();
        var worksets = ReadUserWorksets(document)
                .Select(workset => new DocumentWorksetInfo
                {
                    Name = workset.Name,
                    Kind = workset.Kind.ToString(),
                    IsOpen = workset.IsOpen
                })
                .ToList();
        using var viewCollector = new FilteredElementCollector(document).OfClass(typeof(View));
        var viewCount = viewCollector.Cast<View>().Count(view => !view.IsTemplate);

        return new DocumentInfoData
        {
            FileName = string.IsNullOrWhiteSpace(document.PathName)
                ? document.Title
                : Path.GetFileName(document.PathName),
            RevitVersion = document.Application.VersionNumber,
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

        using var collector = new FilteredElementCollector(document).OfClass(typeof(View));
        var views = collector.Cast<View>()
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
        using var collector = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType();
        foreach (var element in collector)
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
        using var collector = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType();
        var viewIds = collector.ToElementIds()
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
        using var collector = new FilteredElementCollector(document).OfClass(typeof(View));
        var views = collector.Cast<View>()
            .Where(view => !view.IsTemplate);
        return ViewReferenceMatcher.Find(views, name, view => RevitValueReader.GetId(view.Id), view => view.Name,
            view => (view as ViewSheet)?.SheetNumber, view => view.ViewType.ToString(), SheetCollectionName);
    }

    internal static string? SheetCollectionName(View view)
    {
#if REVIT2027_OR_GREATER
        if (view is ViewSheet sheet)
            return view.Document.GetElement(sheet.SheetCollectionId)?.Name;
#endif
        return null;
    }

    public static ViewHeader ReadHeader(Document document, View view, int elementCount)
    {
        var filterIds = view.GetFilters();
        return new ViewHeader
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
