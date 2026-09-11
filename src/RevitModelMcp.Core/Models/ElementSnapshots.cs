using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed class RegionSnapshot
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "familyName")]
    public string? FamilyName { get; set; }

    [DataMember(Name = "typeName")]
    public string? TypeName { get; set; }

    [DataMember(Name = "mark")]
    public string? Mark { get; set; }

    [DataMember(Name = "comment1")]
    public string? Comment1 { get; set; }

    [DataMember(Name = "comment2")]
    public string? Comment2 { get; set; }

    [DataMember(Name = "ownerViewName")]
    public string? OwnerViewName { get; set; }

    [DataMember(Name = "bboxOnViewMm")]
    public BoundingBoxOnViewSnapshot? BboxOnViewMm { get; set; }
}

[DataContract]
public sealed class AnnotationSnapshot
{
    [DataMember(Name = "category")]
    public string? Category { get; set; }

    [DataMember(Name = "familyName")]
    public string? FamilyName { get; set; }

    [DataMember(Name = "typeName")]
    public string? TypeName { get; set; }

    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "params")]
    public Dictionary<string, string> Params { get; set; } = new();
}

[DataContract]
public sealed class CurtainPanelSnapshot
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "familyName")]
    public string? FamilyName { get; set; }

    [DataMember(Name = "typeName")]
    public string? TypeName { get; set; }

    [DataMember(Name = "params")]
    public Dictionary<string, string> Params { get; set; } = new();
}

[DataContract]
public sealed class PanelStatsSnapshot
{
    [DataMember(Name = "panelsOnView")]
    public int PanelsOnView { get; set; }

    [DataMember(Name = "panelsWithSegment")]
    public int PanelsWithSegment { get; set; }

    [DataMember(Name = "panelsWithNumber")]
    public int PanelsWithNumber { get; set; }

    [DataMember(Name = "panelsWithMark")]
    public int PanelsWithMark { get; set; }

    [DataMember(Name = "panelTagsOnView")]
    public int PanelTagsOnView { get; set; }
}

[DataContract]
public sealed class SelectionSnapshot
{
    [DataMember(Name = "category")]
    public string? Category { get; set; }

    [DataMember(Name = "familyName")]
    public string? FamilyName { get; set; }

    [DataMember(Name = "typeName")]
    public string? TypeName { get; set; }

    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "mark")]
    public string? Mark { get; set; }

    [DataMember(Name = "bboxOnViewMm")]
    public BoundingBoxOnViewSnapshot? BboxOnViewMm { get; set; }
}

[DataContract]
public sealed class BoundingBoxOnViewSnapshot
{
    [DataMember(Name = "yMin")]
    public double YMin { get; set; }

    [DataMember(Name = "yMax")]
    public double YMax { get; set; }

    [DataMember(Name = "xMin")]
    public double XMin { get; set; }

    [DataMember(Name = "xMax")]
    public double XMax { get; set; }
}

[DataContract]
public sealed class ModelCountsSnapshot
{
    [DataMember(Name = "totalRegions")]
    public int TotalRegions { get; set; }

    [DataMember(Name = "coordinationViews")]
    public int CoordinationViews { get; set; }

    [DataMember(Name = "constructionViews")]
    public int ConstructionViews { get; set; }

    [DataMember(Name = "sheets")]
    public int Sheets { get; set; }

    [DataMember(Name = "sectionViews")]
    public int SectionViews { get; set; }
}

[DataContract]
public sealed class SectionViewSnapshot
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "name")]
    public string? Name { get; set; }

    [DataMember(Name = "templateName")]
    public string? TemplateName { get; set; }

    [DataMember(Name = "scale")]
    public int Scale { get; set; }

    [DataMember(Name = "cropActive")]
    public bool CropActive { get; set; }

    [DataMember(Name = "cropBboxMm")]
    public SectionCropBoundingBoxSnapshot? CropBboxMm { get; set; }

    [DataMember(Name = "isNameDuplicate")]
    public bool IsNameDuplicate { get; set; }

    [DataMember(Name = "prefix")]
    public string Prefix { get; set; } = "other";
}

[DataContract]
public sealed class SectionCropBoundingBoxSnapshot
{
    [DataMember(Name = "minX")]
    public double MinX { get; set; }

    [DataMember(Name = "minY")]
    public double MinY { get; set; }

    [DataMember(Name = "minZ")]
    public double MinZ { get; set; }

    [DataMember(Name = "maxX")]
    public double MaxX { get; set; }

    [DataMember(Name = "maxY")]
    public double MaxY { get; set; }

    [DataMember(Name = "maxZ")]
    public double MaxZ { get; set; }
}

[DataContract]
public sealed class DuplicateBaseNameSnapshot
{
    [DataMember(Name = "baseName")]
    public string? BaseName { get; set; }

    [DataMember(Name = "count")]
    public int Count { get; set; }
}
