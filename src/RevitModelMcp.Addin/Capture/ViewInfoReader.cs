using System.Runtime.Serialization;
using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Capture;

internal static class ViewInfoReader
{
    public static List<Category> Categories(Document document)
    {
        static IEnumerable<Category> Descendants(Category category)
        {
            yield return category;
            if (category.SubCategories is null) yield break;
            foreach (Category child in category.SubCategories)
                foreach (var descendant in Descendants(child))
                    yield return descendant;
        }

        return document.Settings.Categories.Cast<Category>().SelectMany(Descendants).ToList();
    }

    public static View? FindView(Document document, string reference)
    {
        using var collector = new FilteredElementCollector(document).OfClass(typeof(View));
        return ViewReferenceMatcher.Find(collector.Cast<View>(), reference,
            view => RevitValueReader.GetId(view.Id), view => view.Name);
    }

    public static ViewInfoData Read(Document document, View view)
    {
        var template = document.GetElement(view.ViewTemplateId) as View;
        var result = new ViewInfoData
        {
            Id = RevitValueReader.GetId(view.Id), Name = view.Name, Type = view.ViewType.ToString(),
            IsTemplate = view.IsTemplate, DetailLevel = view.DetailLevel.ToString(),
            DisplayStyle = view.DisplayStyle.ToString(), Discipline = RevitValueReader.GetParameterText(view.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE)),
            Phase = ParameterElementName(document, view, BuiltInParameter.VIEW_PHASE),
            PhaseFilter = ParameterElementName(document, view, BuiltInParameter.VIEW_PHASE_FILTER),
            Scale = view.Scale, Crop = new CropInfo { Active = view.CropBoxActive, Visible = view.CropBoxVisible },
            ClassToggles = new CategoryClassInfo
            {
                ModelHidden = view.AreModelCategoriesHidden, AnnotationHidden = view.AreAnnotationCategoriesHidden,
                AnalyticalHidden = view.AreAnalyticalModelCategoriesHidden, ImportHidden = view.AreImportCategoriesHidden,
                PointCloudsHidden = view.ArePointCloudsHidden
            }
        };
        if (template is not null)
        {
            var nonControlled = template.GetNonControlledTemplateParameterIds().ToHashSet();
            result.Template = new TemplateInfo
            {
                Id = RevitValueReader.GetId(template.Id), Name = template.Name,
                ControlledParameters = template.GetTemplateParameterIds().Where(id => !nonControlled.Contains(id))
                    .Select(id => RevitValueReader.GetId(id) < 0
                        ? LabelUtils.GetLabelFor((BuiltInParameter)RevitValueReader.GetId(id))
                        : document.GetElement(id)?.Name ?? RevitValueReader.GetId(id).ToString())
                    .OrderBy(name => name).ToList()
            };
        }
        if (view is View3D view3D)
        {
            var box = view3D.GetSectionBox();
            result.SectionBox = new SectionBoxInfo
            {
                Active = view3D.IsSectionBoxActive,
                Min = Coordinates(box.Min), Max = Coordinates(box.Max)
            };
        }
        if (view.ViewType is ViewType.ThreeD or ViewType.Section or ViewType.Elevation)
        {
            using var background = view.GetBackground();
            result.Background = new BackgroundInfo { Type = background.Type.ToString() };
            if (background.BackgroundColor is not null)
            {
                if (background.Type == ViewDisplayBackgroundType.Gradient)
                    result.Background.HorizonColor = Color(background.BackgroundColor);
                else
                    result.Background.Color = Color(background.BackgroundColor);
            }
            if (background.SkyColor is not null) result.Background.SkyColor = Color(background.SkyColor);
            if (background.GroundColor is not null) result.Background.GroundColor = Color(background.GroundColor);
        }
        foreach (var category in Categories(document))
        {
            try
            {
                if (view.GetCategoryHidden(category.Id))
                    result.HiddenCategories.Add(new CategoryInfo { Id = RevitValueReader.GetId(category.Id), Name = category.Name, Type = category.CategoryType.ToString() });
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        }
        if (document.IsWorkshared)
        {
            using var collector = new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset);
            result.Worksets = collector.Select(workset => new WorksetInfo
            {
                Name = workset.Name, Visibility = view.GetWorksetVisibility(workset.Id).ToString(),
                EffectiveVisible = view.IsWorksetVisible(workset.Id)
            }).OrderBy(workset => workset.Name).ToList();
        }
        result.Filters = view.GetFilters().Select(id => new FilterInfo
        {
            Name = document.GetElement(id)?.Name ?? RevitValueReader.GetId(id).ToString(),
            Visible = view.GetFilterVisibility(id), Enabled = view.GetIsFilterEnabled(id)
        }).ToList();
        using var links = new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance));
        result.Links = links.Cast<RevitLinkInstance>().Select(link => new LinkInfo
        {
            Name = link.Name, Hidden = LinkHidden(view, link), OverrideType = LinkOverrideType(view, link)
        }).ToList();
        result.TemporaryModes = new TemporaryModeInfo
        {
            HideIsolate = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate),
            ViewProperties = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryViewProperties),
            RevealHiddenElements = view.IsInTemporaryViewMode(TemporaryViewMode.RevealHiddenElements),
            WorksharingDisplay = view.IsInTemporaryViewMode(TemporaryViewMode.WorksharingDisplay)
        };
        return result;
    }

    private static bool LinkHidden(View view, RevitLinkInstance link)
    {
        try { return link.IsHidden(view) || link.Category is not null && view.GetCategoryHidden(link.Category.Id); }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return link.IsHidden(view); }
    }

    private static string? LinkOverrideType(View view, RevitLinkInstance link)
    {
#if REVIT2024_OR_GREATER
        try
        {
            using var settings = view.GetLinkOverrides(link.Id);
            return settings?.LinkVisibilityType.ToString();
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
#endif
        return null;
    }

    private static string? ParameterElementName(Document document, View view, BuiltInParameter parameter)
    {
        var id = view.get_Parameter(parameter)?.AsElementId();
        return RevitValueReader.IsValidId(id) ? document.GetElement(id)?.Name : null;
    }

    private static double[] Coordinates(XYZ point) =>
        [RevitValueReader.ToMillimeters(point.X), RevitValueReader.ToMillimeters(point.Y), RevitValueReader.ToMillimeters(point.Z)];

    private static string Color(Color color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}

[DataContract]
internal sealed class ViewInfoData
{
    [DataMember(Name = "id")] public long Id { get; set; }
    [DataMember(Name = "name")] public string Name { get; set; } = "";
    [DataMember(Name = "type")] public string Type { get; set; } = "";
    [DataMember(Name = "isTemplate")] public bool IsTemplate { get; set; }
    [DataMember(Name = "template")] public TemplateInfo? Template { get; set; }
    [DataMember(Name = "detailLevel")] public string DetailLevel { get; set; } = "";
    [DataMember(Name = "displayStyle")] public string DisplayStyle { get; set; } = "";
    [DataMember(Name = "discipline")] public string? Discipline { get; set; }
    [DataMember(Name = "phase")] public string? Phase { get; set; }
    [DataMember(Name = "phaseFilter")] public string? PhaseFilter { get; set; }
    [DataMember(Name = "scale")] public int Scale { get; set; }
    [DataMember(Name = "crop")] public CropInfo Crop { get; set; } = new();
    [DataMember(Name = "sectionBox")] public SectionBoxInfo? SectionBox { get; set; }
    [DataMember(Name = "background")] public BackgroundInfo? Background { get; set; }
    [DataMember(Name = "classToggles")] public CategoryClassInfo ClassToggles { get; set; } = new();
    [DataMember(Name = "hiddenCategories")] public List<CategoryInfo> HiddenCategories { get; set; } = [];
    [DataMember(Name = "worksets")] public List<WorksetInfo>? Worksets { get; set; }
    [DataMember(Name = "filters")] public List<FilterInfo> Filters { get; set; } = [];
    [DataMember(Name = "links")] public List<LinkInfo> Links { get; set; } = [];
    [DataMember(Name = "temporaryModes")] public TemporaryModeInfo TemporaryModes { get; set; } = new();
}
[DataContract] internal sealed class TemplateInfo { [DataMember(Name = "id")] public long Id { get; set; } [DataMember(Name = "name")] public string Name { get; set; } = ""; [DataMember(Name = "controlledParameters")] public List<string> ControlledParameters { get; set; } = []; }
[DataContract] internal sealed class CropInfo { [DataMember(Name = "active")] public bool Active { get; set; } [DataMember(Name = "visible")] public bool Visible { get; set; } }
[DataContract] internal sealed class SectionBoxInfo { [DataMember(Name = "active")] public bool Active { get; set; } [DataMember(Name = "min")] public double[] Min { get; set; } = []; [DataMember(Name = "max")] public double[] Max { get; set; } = []; }
[DataContract] internal sealed class BackgroundInfo { [DataMember(Name = "type")] public string Type { get; set; } = ""; [DataMember(Name = "color")] public string? Color { get; set; } [DataMember(Name = "skyColor")] public string? SkyColor { get; set; } [DataMember(Name = "horizonColor")] public string? HorizonColor { get; set; } [DataMember(Name = "groundColor")] public string? GroundColor { get; set; } }
[DataContract] internal sealed class CategoryClassInfo { [DataMember(Name = "modelHidden")] public bool ModelHidden { get; set; } [DataMember(Name = "annotationHidden")] public bool AnnotationHidden { get; set; } [DataMember(Name = "analyticalHidden")] public bool AnalyticalHidden { get; set; } [DataMember(Name = "importHidden")] public bool ImportHidden { get; set; } [DataMember(Name = "pointCloudsHidden")] public bool PointCloudsHidden { get; set; } }
[DataContract] internal sealed class CategoryInfo { [DataMember(Name = "id")] public long Id { get; set; } [DataMember(Name = "name")] public string Name { get; set; } = ""; [DataMember(Name = "type")] public string Type { get; set; } = ""; }
[DataContract] internal sealed class WorksetInfo { [DataMember(Name = "name")] public string Name { get; set; } = ""; [DataMember(Name = "visibility")] public string Visibility { get; set; } = ""; [DataMember(Name = "effectiveVisible")] public bool EffectiveVisible { get; set; } }
[DataContract] internal sealed class FilterInfo { [DataMember(Name = "name")] public string Name { get; set; } = ""; [DataMember(Name = "visible")] public bool Visible { get; set; } [DataMember(Name = "enabled")] public bool Enabled { get; set; } }
[DataContract] internal sealed class LinkInfo { [DataMember(Name = "name")] public string Name { get; set; } = ""; [DataMember(Name = "hidden")] public bool Hidden { get; set; } [DataMember(Name = "overrideType", EmitDefaultValue = false)] public string? OverrideType { get; set; } }
[DataContract] internal sealed class TemporaryModeInfo { [DataMember(Name = "hideIsolate")] public bool HideIsolate { get; set; } [DataMember(Name = "viewProperties")] public bool ViewProperties { get; set; } [DataMember(Name = "revealHiddenElements")] public bool RevealHiddenElements { get; set; } [DataMember(Name = "worksharingDisplay")] public bool WorksharingDisplay { get; set; } }
