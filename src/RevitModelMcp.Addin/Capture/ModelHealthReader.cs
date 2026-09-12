using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class ModelHealthReader
{
    public static ModelHealthData Read(Document document, ControlJobContract job)
    {
        var result = new ModelHealthData
        {
            RevitVersion = document.Application.VersionNumber,
            RevitBuild = document.Application.VersionBuild,
            FileName = Path.GetFileName(document.PathName),
            IsWorkshared = document.IsWorkshared
        };
        if (string.IsNullOrEmpty(result.FileName)) result.FileName = document.Title;
        result.FileSizeBytes = Try<long?>(result, "fileSizeBytes", () =>
            string.IsNullOrEmpty(document.PathName) ? null : new FileInfo(document.PathName).Length);
        foreach (var metric in new Dictionary<string, Func<string>>
        {
            ["name"] = () => document.ProjectInformation.Name,
            ["number"] = () => document.ProjectInformation.Number,
            ["client"] = () => document.ProjectInformation.ClientName,
            ["address"] = () => document.ProjectInformation.Address,
            ["buildingName"] = () => document.ProjectInformation.BuildingName,
            ["status"] = () => document.ProjectInformation.Status,
            ["author"] = () => document.ProjectInformation.Author
        }) result.ProjectInfo[metric.Key] = Try(result, "projectInfo." + metric.Key, metric.Value);

        var metrics = new Dictionary<string, Func<int>>
        {
            ["elements"] = () => Count(document, collector => collector.WhereElementIsNotElementType()),
            ["warnings"] = () => document.GetWarnings().Count,
            ["warningGroups"] = () => document.GetWarnings().Select(warning => warning.GetDescriptionText()).Distinct().Count(),
            ["levels"] = () => CountClass<Level>(document),
            ["grids"] = () => CountClass<Grid>(document),
            ["views"] = () => CountClass<View>(document),
            ["viewsNotOnSheets"] = () => ViewsNotOnSheets(document),
            ["viewTemplates"] = () => Views(document).Count(view => view.IsTemplate),
            ["sheets"] = () => CountClass<ViewSheet>(document),
            ["rooms"] = () => CountCategory(document, BuiltInCategory.OST_Rooms),
            ["roomsUnplaced"] = () => Rooms(document).Count(room => room.Area <= 0 && room.Location is null),
            ["roomsNotEnclosed"] = () => Rooms(document).Count(room => room.Area <= 0 && room.Location is not null),
            ["families"] = () => CountClass<Family>(document),
            ["familiesInPlace"] = () => new FilteredElementCollector(document).OfClass(typeof(Family))
                .Cast<Family>().Count(family => family.IsInPlace),
            ["familyTypesUnused"] = () => UnusedTypes(document),
            ["groupsModel"] = () => CountCategory(document, BuiltInCategory.OST_IOSModelGroups),
            ["groupsDetail"] = () => CountCategory(document, BuiltInCategory.OST_IOSDetailGroups),
            ["groupTypes"] = () => CountClass<GroupType>(document),
            ["designOptions"] = () => CountClass<DesignOption>(document),
            ["worksets"] = () => document.IsWorkshared
                ? new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset).ToWorksets().Count : 0,
            ["linksRvt"] = () => CountClass<RevitLinkType>(document),
            ["linksCad"] = () => new FilteredElementCollector(document).OfClass(typeof(CADLinkType))
                .Cast<CADLinkType>().Count(type => type.IsExternalFileReference()),
            ["cadImports"] = () => Imports(document).Count(instance => !instance.IsLinked),
            ["images"] = () => CountClass<ImageInstance>(document)
        };
        foreach (var metric in metrics)
            result.Counts[metric.Key] = Try<int?>(result, "counts." + metric.Key, () => metric.Value());
        result.TopWarnings = Try(result, "topWarnings", () => ModelWarningReader.Read(document, null, false)
            .Groups.Take(10).Select(group => new HealthWarning { Text = group.Text, Count = group.Count }).ToList())!;
        foreach (var unit in new Dictionary<string, ForgeTypeId>
        {
            ["length"] = SpecTypeId.Length,
            ["area"] = SpecTypeId.Area,
            ["volume"] = SpecTypeId.Volume
        }) result.Units[unit.Key] = Try(result, "units." + unit.Key,
            () => document.GetUnits().GetFormatOptions(unit.Value).GetUnitTypeId().TypeId);
        return result;
    }

    private static T? Try<T>(ModelHealthData result, string metric, Func<T> read)
    {
        try { return read(); }
        catch (Exception exception)
        {
            result.Skipped.Add(new SkippedMetric { Metric = metric, Error = exception.Message });
            return default;
        }
    }

    private static int Count(Document document, Func<FilteredElementCollector, FilteredElementCollector> filter)
    {
        using var collector = new FilteredElementCollector(document);
        return filter(collector).GetElementCount();
    }

    private static int CountClass<T>(Document document) => Count(document, collector => collector.OfClass(typeof(T)));
    private static int CountCategory(Document document, BuiltInCategory category) =>
        Count(document, collector => collector.OfCategory(category).WhereElementIsNotElementType());
    private static IEnumerable<View> Views(Document document) =>
        new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>();
    private static IEnumerable<Room> Rooms(Document document) =>
        new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>();
    private static IEnumerable<ImportInstance> Imports(Document document) =>
        new FilteredElementCollector(document).OfClass(typeof(ImportInstance)).Cast<ImportInstance>();

    private static int ViewsNotOnSheets(Document document)
    {
        var placed = new HashSet<ElementId>(new FilteredElementCollector(document).OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>().SelectMany(sheet => sheet.GetAllPlacedViews()));
        return Views(document).Count(view => !view.IsTemplate && !placed.Contains(view.Id) &&
            view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan
                or ViewType.AreaPlan or ViewType.Section or ViewType.Elevation or ViewType.ThreeD
                or ViewType.DraftingView or ViewType.Legend);
    }

    private static int UnusedTypes(Document document)
    {
        using var instances = new FilteredElementCollector(document).WhereElementIsNotElementType();
        var used = new HashSet<ElementId>(instances.Select(element => element.GetTypeId()));
        using var types = new FilteredElementCollector(document).WhereElementIsElementType();
        return types.Count(type => !used.Contains(type.Id));
    }
}
