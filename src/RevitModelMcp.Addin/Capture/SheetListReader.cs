using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class SheetListReader
{
    public static SheetListData Read(Document document, ControlJobParseResult job)
    {
        var sheets = Collect<ViewSheet>(document).OrderBy(sheet => sheet.SheetNumber, StringComparer.Ordinal)
            .ThenBy(sheet => RevitValueReader.GetId(sheet.Id)).ToList();
        var placements = new Dictionary<long, HashSet<long>>();
        var viewportCounts = new Dictionary<long, int>();
        foreach (var element in Collect<Viewport>(document).Cast<Element>()
                     .Concat(Collect<ScheduleSheetInstance>(document))
                     .Concat(Collect<PanelScheduleSheetInstance>(document)))
        {
            try
            {
                if (element is ScheduleSheetInstance { IsTitleblockRevisionSchedule: true }) continue;
                var sheetId = RevitValueReader.GetId(element is Viewport viewport ? viewport.SheetId : element.OwnerViewId);
                var viewId = RevitValueReader.GetId(element switch
                {
                    Viewport placement => placement.ViewId,
                    ScheduleSheetInstance schedule => schedule.ScheduleId,
                    PanelScheduleSheetInstance panel => panel.ScheduleId,
                    _ => ElementId.InvalidElementId
                });
                if (!placements.TryGetValue(sheetId, out var viewIds))
                    placements[sheetId] = viewIds = [];
                viewIds.Add(viewId);
                if (element is Viewport)
                    viewportCounts[sheetId] = viewportCounts.GetValueOrDefault(sheetId) + 1;
            }
            catch (Exception exception) { PluginLog.Skipped($"sheet placement {RevitValueReader.GetId(element.Id)}", exception); }
        }

        using var titleBlocks = document.CollectElements().OfCategory(BuiltInCategory.OST_TitleBlocks).Instances();
        var blocks = titleBlocks.ToElements().ToLookup(block => RevitValueReader.GetId(block.OwnerViewId));
        var rows = new List<SheetListItem>();
        foreach (var sheet in sheets.Skip(job.Offset).Take(job.Limit))
        {
            var sheetId = RevitValueReader.GetId(sheet.Id);
            try
            {
                var block = blocks[sheetId].OrderBy(element => RevitValueReader.GetId(element.Id)).FirstOrDefault();
                rows.Add(new SheetListItem
                {
                    Id = sheetId,
                    Number = sheet.SheetNumber,
                    Name = sheet.Name,
                    TitleBlock = block is null ? null : document.GetElement(block.GetTypeId())?.Name,
                    RevisionCount = sheet.GetAllRevisionIds().Count,
                    ViewportCount = viewportCounts.GetValueOrDefault(sheetId),
                    ViewIds = placements.TryGetValue(sheetId, out var viewIds) ? viewIds.OrderBy(id => id).ToList() : []
                });
            }
            catch (Exception exception) { PluginLog.Skipped($"sheet {sheetId}", exception); }
        }

        var unplaced = new List<UnplacedViewItem>();
        if (job.IncludeUnplacedViews)
        {
            var placed = new HashSet<long>(placements.Values.SelectMany(ids => ids));
            foreach (var view in Collect<View>(document))
            {
                var viewId = RevitValueReader.GetId(view.Id);
                try
                {
                    if (view.IsTemplate || placed.Contains(viewId) || !CanPlace(view)) continue;
                    unplaced.Add(new UnplacedViewItem { Id = viewId, Name = view.Name, ViewType = view.ViewType.ToString() });
                }
                catch (Exception exception) { PluginLog.Skipped($"unplaced view {viewId}", exception); }
            }
        }
        return new SheetListData
        {
            Sheets = rows,
            UnplacedViews = unplaced.OrderBy(view => view.Id).ToList(),
            Total = sheets.Count,
            Offset = job.Offset,
            Limit = job.Limit,
            HasMore = (long)job.Offset + job.Limit < sheets.Count
        };
    }

    private static bool CanPlace(View view)
    {
        if (view is ViewSchedule schedule)
            return !schedule.IsTitleblockRevisionSchedule && !schedule.IsInternalKeynoteSchedule;
        if (view is PanelScheduleView panel) return !panel.IsPanelScheduleTemplate();
        return view.CanBePrinted && view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan
            or ViewType.EngineeringPlan or ViewType.AreaPlan or ViewType.Elevation or ViewType.Section
            or ViewType.Detail or ViewType.DraftingView or ViewType.ThreeD or ViewType.Legend or ViewType.Rendering;
    }

    private static List<T> Collect<T>(Document document) where T : Element
    {
        using var collector = document.CollectElements().OfClass<T>();
        return collector.Cast<T>().ToList();
    }
}
