using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed class Snapshot
{
    [DataMember(Name = "meta")]
    public SnapshotMetadata Meta { get; set; } = new();

    [DataMember(Name = "responder")]
    public ResponderInfo Responder { get; set; } = new();

    [DataMember(Name = "activeView")]
    public ActiveViewSnapshot ActiveView { get; set; } = new();

    [DataMember(Name = "regions")]
    public List<RegionSnapshot> Regions { get; set; } = new();

    [DataMember(Name = "regionCount")]
    public int RegionCount { get; set; }

    [DataMember(Name = "elementsOnView")]
    public Dictionary<string, int> ElementsOnView { get; set; } = new();

    [DataMember(Name = "annotations")]
    public List<AnnotationSnapshot> Annotations { get; set; } = new();

    [DataMember(Name = "curtainPanels")]
    public List<CurtainPanelSnapshot> CurtainPanels { get; set; } = new();

    [DataMember(Name = "curtainPanelsCapped")]
    public bool CurtainPanelsCapped { get; set; }

    [DataMember(Name = "panelStats")]
    public PanelStatsSnapshot PanelStats { get; set; } = new();

    [DataMember(Name = "selection")]
    public List<SelectionSnapshot> Selection { get; set; } = new();

    [DataMember(Name = "modelCounts")]
    public ModelCountsSnapshot ModelCounts { get; set; } = new();

    [DataMember(Name = "allSectionViews")]
    public List<SectionViewSnapshot> AllSectionViews { get; set; } = new();

    [DataMember(Name = "duplicateBaseNames")]
    public List<DuplicateBaseNameSnapshot> DuplicateBaseNames { get; set; } = new();
}

[DataContract]
public sealed class SnapshotMetadata
{
    [DataMember(Name = "utc")]
    public string? Utc { get; set; }

    [DataMember(Name = "local")]
    public string? Local { get; set; }

    [DataMember(Name = "documentTitle")]
    public string? DocumentTitle { get; set; }

    [DataMember(Name = "documentPath")]
    public string? DocumentPath { get; set; }

    [DataMember(Name = "revitVersion")]
    public string? RevitVersion { get; set; }

    [DataMember(Name = "activeViewId")]
    public long? ActiveViewId { get; set; }
}

[DataContract]
public sealed class InstanceStatus
{
    [DataMember(Name = "processId", Order = 1)]
    public int ProcessId { get; set; }

    [DataMember(Name = "revitVersion", Order = 2)]
    public string RevitVersion { get; set; } = string.Empty;

    [DataMember(Name = "documentTitle", Order = 3)]
    public string DocumentTitle { get; set; } = string.Empty;

    [DataMember(Name = "documentPath", Order = 4)]
    public string DocumentPath { get; set; } = string.Empty;

    [DataMember(Name = "updatedUtc", Order = 5)]
    public string UpdatedUtc { get; set; } = string.Empty;

    [DataMember(Name = "fileChannelVersion", Order = 6)]
    public int FileChannelVersion { get; set; } = 2;

    [DataMember(Name = "startedUtc", Order = 7)]
    public string StartedUtc { get; set; } = string.Empty;

    [DataMember(Name = "httpPort", Order = 8)]
    public int? HttpPort { get; set; }

    [DataMember(Name = "discoveryVersion", Order = 9)]
    public int DiscoveryVersion { get; set; } = 3;

    [DataMember(Name = "instanceId", Order = 10)]
    public string InstanceId { get; set; } = string.Empty;

    [DataMember(Name = "pipeName", Order = 11, EmitDefaultValue = false)]
    public string? PipeName { get; set; }

    [DataMember(Name = "protocols", Order = 12)]
    public List<string> Protocols { get; set; } = new();

    [DataMember(Name = "documents", Order = 13)]
    public List<InstanceDocument> Documents { get; set; } = new();
}

[DataContract]
public sealed class InstanceDocument
{
    [DataMember(Name = "title", Order = 1)]
    public string Title { get; set; } = string.Empty;

    [DataMember(Name = "path", Order = 2)]
    public string Path { get; set; } = string.Empty;

    [DataMember(Name = "isActive", Order = 3)]
    public bool IsActive { get; set; }

    [DataMember(Name = "isFamilyDocument", Order = 4)]
    public bool IsFamilyDocument { get; set; }
}

[DataContract]
public sealed class ActiveViewSnapshot
{
    [DataMember(Name = "name")]
    public string? Name { get; set; }

    [DataMember(Name = "id")]
    public long? Id { get; set; }

    [DataMember(Name = "viewType")]
    public string? ViewType { get; set; }

    [DataMember(Name = "scale")]
    public int? Scale { get; set; }

    [DataMember(Name = "detailLevel")]
    public string? DetailLevel { get; set; }

    [DataMember(Name = "viewTemplateName")]
    public string? ViewTemplateName { get; set; }

    [DataMember(Name = "cropBoxActive")]
    public bool? CropBoxActive { get; set; }

    [DataMember(Name = "cropBox")]
    public CropBoxSnapshot? CropBox { get; set; }

    [DataMember(Name = "cropHeightMm")]
    public double? CropHeightMm { get; set; }

    [DataMember(Name = "cropWidthMm")]
    public double? CropWidthMm { get; set; }

    [DataMember(Name = "customCropLoops")]
    public int? CustomCropLoops { get; set; }

    [DataMember(Name = "scopeBoxName")]
    public string? ScopeBoxName { get; set; }

    [DataMember(Name = "isSection")]
    public bool? IsSection { get; set; }

    [DataMember(Name = "viewDirection")]
    public VectorSnapshot? ViewDirection { get; set; }

    [DataMember(Name = "originMm")]
    public VectorSnapshot? OriginMm { get; set; }

    [DataMember(Name = "farClipOffsetMm")]
    public double? FarClipOffsetMm { get; set; }

    [DataMember(Name = "isDependent")]
    public bool? IsDependent { get; set; }

    [DataMember(Name = "primaryViewName")]
    public string? PrimaryViewName { get; set; }

    [DataMember(Name = "dependentsCount")]
    public int? DependentsCount { get; set; }
}

[DataContract]
public sealed class CropBoxSnapshot
{
    [DataMember(Name = "minXmm")]
    public double MinXmm { get; set; }

    [DataMember(Name = "minYmm")]
    public double MinYmm { get; set; }

    [DataMember(Name = "minZmm")]
    public double MinZmm { get; set; }

    [DataMember(Name = "maxXmm")]
    public double MaxXmm { get; set; }

    [DataMember(Name = "maxYmm")]
    public double MaxYmm { get; set; }

    [DataMember(Name = "maxZmm")]
    public double MaxZmm { get; set; }
}

[DataContract]
public sealed class VectorSnapshot
{
    [DataMember(Name = "x")]
    public double X { get; set; }

    [DataMember(Name = "y")]
    public double Y { get; set; }

    [DataMember(Name = "z")]
    public double Z { get; set; }
}
