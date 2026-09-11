using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class RelationReader
{
    public static RelationsData Read(Document document, ControlJobParseResult job)
    {
        return job.Relation switch
        {
            "level-rooms" => ReadLevelRooms(document, job.SourceName!),
            "group-elements" => ReadGroup(document, job.SourceId!.Value),
            "nested-family" => ReadNestedFamily(document, job.SourceId!.Value),
            "area-scheme-elements" => ReadAreaScheme(document, job.SourceName!),
            "view-template-dependents" => ReadTemplateViews(document, job.SourceName!),
            _ => throw new ArgumentOutOfRangeException(nameof(job.Relation), job.Relation, "Unknown relation.")
        };
    }

    private static RelationsData ReadLevelRooms(Document document, string name)
    {
        var level = FindByName<Level>(document, name, "Level", "levels");
        var rooms = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType().WherePasses(new ElementLevelFilter(level.Id)).OfType<Room>();
        return Create(document, "level-rooms", level, rooms);
    }

    private static RelationsData ReadGroup(Document document, long id)
    {
        var group = GetElement(document, id) as Group
                    ?? throw new ArgumentException($"Group with id {id} was not found.");
        return Create(document, "group-elements", group, group.GetMemberIds().Select(document.GetElement));
    }

    private static RelationsData ReadNestedFamily(Document document, long id)
    {
        var family = GetElement(document, id) as FamilyInstance
                     ?? throw new ArgumentException($"Family instance with id {id} was not found.");
        return Create(document, "nested-family", family, family.GetSubComponentIds().Select(document.GetElement));
    }

    private static RelationsData ReadAreaScheme(Document document, string name)
    {
        var scheme = FindByName<AreaScheme>(document, name, "Area scheme", "area-schemes");
        var areas = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_Areas)
            .WhereElementIsNotElementType()
            .WherePasses(new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(
                BuiltInId(BuiltInParameter.AREA_SCHEME_ID), scheme.Id)));
        return Create(document, "area-scheme-elements", scheme, areas);
    }

    private static RelationsData ReadTemplateViews(Document document, string name)
    {
        var template = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(view => view.IsTemplate && string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException(
                           $"View template '{name}' was not found. Use list-catalog with section=views.");
        var views = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
            .Where(view => !view.IsTemplate && view.ViewTemplateId == template.Id);
        return Create(document, "view-template-dependents", template, views);
    }

    private static RelationsData Create(
        Document document,
        string relation,
        Element source,
        IEnumerable<Element?> elements)
    {
        return new RelationsData
        {
            Relation = relation,
            Source = ToRelation(document, source),
            Elements = elements.Where(element => element is not null)
                .Select(element => ToRelation(document, element!))
                .OrderBy(element => element.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(element => element.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static RelationElement ToRelation(Document document, Element element) => new()
    {
        Id = RevitValueReader.GetId(element.Id),
        Name = element.Name,
        Category = element.Category?.Name,
        Family = RevitValueReader.GetFamilyName(element),
        Type = RevitValueReader.GetTypeName(document, element)
    };

    private static T FindByName<T>(Document document, string name, string kind, string section) where T : Element =>
        new FilteredElementCollector(document).OfClass(typeof(T)).Cast<T>()
            .FirstOrDefault(element => string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"{kind} '{name}' was not found. Use list-catalog with section={section}.");

    private static Element? GetElement(Document document, long id) => document.GetElement(CreateElementId(id));

    private static ElementId BuiltInId(BuiltInParameter parameter) => CreateElementId((long)parameter);

    private static ElementId CreateElementId(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }
}
