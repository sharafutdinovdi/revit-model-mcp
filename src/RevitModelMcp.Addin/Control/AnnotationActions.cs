using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class AnnotationActions
{
    /// <summary>Creates one tag per resolvable element in a view; inputs that cannot be tagged are reported in <c>skipped</c>.</summary>
    internal static ActionResultData TagElements(Document document, ActionJobContract action)
    {
        var view = ReadCommandReader.FindView(document, action.View!)
            ?? throw new ArgumentException($"View '{action.View}' was not found.");
        if (view is ViewSheet or ViewSchedule or ViewDrafting)
            throw new ArgumentException($"View '{view.Name}' cannot hold element tags; use a plan, section, elevation or 3D view.");

        var skipped = new List<string>();
        var elements = ResolveElements(document, view, action, skipped);
        var tagged = action.SkipTagged ? TaggedElementIds(document, view) : [];
        var independentTypes = ResolveIndependentTagTypes(document, action.TypeName);
        var roomTagType = ResolveRoomTagType(document, action.TypeName);
        if (action.TypeName is not null && independentTypes.Count == 0 && roomTagType is null)
            throw new ArgumentException($"Tag type '{action.TypeName}' was not found.");

        var orientation = action.Orientation == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
        var created = new List<long>();
        foreach (var element in elements)
        {
            var id = RevitValueReader.GetId(element.Id);
            if (tagged.Contains(id))
            {
                skipped.Add($"Element {id}: already tagged in this view.");
                continue;
            }
            try
            {
                var tagId = element is Room room
                    ? TagRoom(document, view, room, action, roomTagType)
                    : TagElement(document, view, element, action, orientation, independentTypes);
                if (tagId is null)
                {
                    skipped.Add(SkipReason(id, element));
                    continue;
                }
                created.Add(RevitValueReader.GetId(tagId));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                skipped.Add($"Element {id}: {exception.Message.Split('\n')[0].Trim()}");
            }
        }
        if (created.Count == 0)
            throw new InvalidOperationException("No tags were created. " + string.Join(" ", skipped.Take(5)));

        var result = new ActionResultData
        {
            Count = created.Count,
            CreatedElementIds = created,
            ViewId = RevitValueReader.GetId(view.Id),
            ViewName = view.Name,
            Category = "Tag",
            Verification = new ActionVerification { Changed = [.. created] }
        };
        if (skipped.Count > 0)
        {
            result.Skipped = skipped;
            result.Warning = $"Skipped {skipped.Count} input(s); see skipped.";
        }
        return result;
    }

    private static List<Element> ResolveElements(Document document, View view, ActionJobContract action, List<string> skipped)
    {
        var result = new List<Element>();
        var seen = new HashSet<long>();
        void Add(Element element)
        {
            if (seen.Add(RevitValueReader.GetId(element.Id))) result.Add(element);
        }
        if (action.ElementIds.Count > 0)
        {
            foreach (var id in action.ElementIds)
            {
                var element = document.GetElement(ActionCommandExecutor.CreateId(id));
                if (element is null) skipped.Add($"Element {id}: not found.");
                else if (element is ElementType) skipped.Add($"Element {id}: is a type, not an instance.");
                else Add(element);
            }
            return result;
        }
        var categories = document.Settings.Categories.Cast<Category>().ToList();
        foreach (var name in action.Categories!)
        {
            var category = ViewVisibility.ResolveCategory(name, categories);
            if (category is null)
            {
                skipped.Add($"Category '{name}': not found.");
                continue;
            }
            using var collector = new FilteredElementCollector(document, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType();
            foreach (var element in collector) Add(element);
            if (result.Count > action.MaxElements) throw new ActionMutations.MatchLimitException(result.Count, action.MaxElements);
        }
        return result;
    }

    private static HashSet<long> TaggedElementIds(Document document, View view)
    {
        var tagged = new HashSet<long>();
        using (var independent = new FilteredElementCollector(document, view.Id).OfClass(typeof(IndependentTag)))
            foreach (IndependentTag tag in independent)
                foreach (var id in tag.GetTaggedLocalElementIds()) tagged.Add(RevitValueReader.GetId(id));
        using (var rooms = new FilteredElementCollector(document, view.Id).OfClass(typeof(RoomTag)))
            foreach (RoomTag tag in rooms)
                if (tag.Room is { } room) tagged.Add(RevitValueReader.GetId(room.Id));
        return tagged;
    }

    private static List<FamilySymbol> ResolveIndependentTagTypes(Document document, string? name)
    {
        if (name is null) return [];
        using var collector = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol));
        return collector.Cast<FamilySymbol>().Where(symbol => symbol.Category is { } category &&
                (QueryFilterBuilder.GetBuiltInCategoryName(category)?.EndsWith("Tags", StringComparison.Ordinal) ?? false) &&
                NameMatches(name, symbol.FamilyName, symbol.Name))
            .ToList();
    }

    private static RoomTagType? ResolveRoomTagType(Document document, string? name)
    {
        if (name is null) return null;
        using var collector = new FilteredElementCollector(document).OfClass(typeof(RoomTagType));
        return collector.Cast<RoomTagType>().FirstOrDefault(type => NameMatches(name, type.FamilyName, type.Name));
    }

    private static bool NameMatches(string requested, string family, string type) =>
        string.Equals(type, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(family, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals($"{family}: {type}", requested, StringComparison.OrdinalIgnoreCase);

    private static ElementId? TagElement(Document document, View view, Element element, ActionJobContract action,
        TagOrientation orientation, List<FamilySymbol> types)
    {
        if (element is SpatialElement || element.Category is null) return null;
        var center = Center(element, view) ?? throw new InvalidOperationException("The element has no location in this view.");
        var head = action.Leader ? center + (view.RightDirection + view.UpDirection) * Millimeters(15) * view.Scale : center;
        var tag = IndependentTag.Create(document, view.Id, new Reference(element), action.Leader,
            TagMode.TM_ADDBY_CATEGORY, orientation, head);
        if (action.TypeName is null) return tag.Id;
        var valid = tag.GetValidTypes();
        var match = types.FirstOrDefault(symbol => valid.Contains(symbol.Id));
        if (match is null)
        {
            document.Delete(tag.Id);
            throw new InvalidOperationException($"Tag type '{action.TypeName}' does not fit this category.");
        }
        tag.ChangeTypeId(match.Id);
        return tag.Id;
    }

    private static ElementId? TagRoom(Document document, View view, Room room, ActionJobContract action, RoomTagType? type)
    {
        if (view is not ViewPlan) throw new InvalidOperationException("Room tags need a plan view.");
        if (room.Location is null || room.Area <= 0) throw new InvalidOperationException("The room is not placed or enclosed.");
        if (action.TypeName is not null && type is null) throw new InvalidOperationException($"Tag type '{action.TypeName}' is not a room tag type.");
        var center = Center(room, view) ?? throw new InvalidOperationException("The room has no location in this view.");
        var tag = document.Create.NewRoomTag(new LinkElementId(room.Id), new UV(center.X, center.Y), view.Id);
        if (tag is null) throw new InvalidOperationException("Revit did not create the room tag.");
        tag.HasLeader = action.Leader;
        if (type is not null) tag.RoomTagType = type;
        return tag.Id;
    }

    private static XYZ? Center(Element element, View view)
    {
        using var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        if (box is not null) return (box.Min + box.Max) / 2;
        return element.Location switch
        {
            LocationPoint point => point.Point,
            LocationCurve { Curve: { } curve } => curve.Evaluate(0.5, true),
            _ => null
        };
    }

    private static string SkipReason(long id, Element element) => element is SpatialElement
        ? $"Element {id}: tags for this spatial element kind are not supported; only rooms can be tagged."
        : $"Element {id}: has no category and cannot be tagged.";

    private static double Millimeters(double value) => ActionCommandExecutor.Millimeters(value);
}
