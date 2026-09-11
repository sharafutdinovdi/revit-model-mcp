using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed class ResponderInfo
{
    [DataMember(Name = "documentName")]
    public string DocumentName { get; set; } = string.Empty;

    [DataMember(Name = "documentPath")]
    public string DocumentPath { get; set; } = string.Empty;

    [DataMember(Name = "processId")]
    public int ProcessId { get; set; }

    [DataMember(Name = "revitVersion")]
    public string RevitVersion { get; set; } = string.Empty;
}

[DataContract]
public sealed class CatalogData
{
    [DataMember(Name = "section")]
    public string Section { get; set; } = string.Empty;

    [DataMember(Name = "items")]
    public List<CatalogItem> Items { get; set; } = new();
}

[DataContract]
public sealed class CatalogItem
{
    [DataMember(Name = "id", EmitDefaultValue = false)]
    public long? Id { get; set; }

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "category", EmitDefaultValue = false)]
    public string? Category { get; set; }

    [DataMember(Name = "family", EmitDefaultValue = false)]
    public string? Family { get; set; }

    [DataMember(Name = "type", EmitDefaultValue = false)]
    public string? Type { get; set; }

    [DataMember(Name = "valueType", EmitDefaultValue = false)]
    public string? ValueType { get; set; }

    [DataMember(Name = "count", EmitDefaultValue = false)]
    public int? Count { get; set; }

    [DataMember(Name = "isTemplate", EmitDefaultValue = false)]
    public bool? IsTemplate { get; set; }

    [DataMember(Name = "isGrossBuildingArea", EmitDefaultValue = false)]
    public bool? IsGrossBuildingArea { get; set; }

    [DataMember(Name = "categories", EmitDefaultValue = false)]
    public List<string>? Categories { get; set; }
}

[DataContract]
public sealed class ModelWarningsData
{
    [DataMember(Name = "totalWarnings")]
    public int TotalWarnings { get; set; }

    [DataMember(Name = "groups")]
    public List<ModelWarningGroup> Groups { get; set; } = new();
}

[DataContract]
public sealed class ModelWarningGroup
{
    [DataMember(Name = "text")]
    public string Text { get; set; } = string.Empty;

    [DataMember(Name = "severity")]
    public string Severity { get; set; } = string.Empty;

    [DataMember(Name = "count")]
    public int Count { get; set; }

    [DataMember(Name = "affectedElementCount")]
    public int AffectedElementCount { get; set; }

    [DataMember(Name = "elements", EmitDefaultValue = false)]
    public List<ModelWarningElement>? Elements { get; set; }
}

[DataContract]
public sealed class ModelWarningElement
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "category", EmitDefaultValue = false)]
    public string? Category { get; set; }

    [DataMember(Name = "name", EmitDefaultValue = false)]
    public string? Name { get; set; }
}

[DataContract]
public sealed class RelationsData
{
    [DataMember(Name = "relation")]
    public string Relation { get; set; } = string.Empty;

    [DataMember(Name = "source")]
    public RelationElement Source { get; set; } = new();

    [DataMember(Name = "elements")]
    public List<RelationElement> Elements { get; set; } = new();
}

[DataContract]
public sealed class RelationElement
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "category", EmitDefaultValue = false)]
    public string? Category { get; set; }

    [DataMember(Name = "family", EmitDefaultValue = false)]
    public string? Family { get; set; }

    [DataMember(Name = "type", EmitDefaultValue = false)]
    public string? Type { get; set; }
}

[DataContract]
public sealed class RoomDetails
{
    [DataMember(Name = "level", EmitDefaultValue = false)]
    public string? Level { get; set; }

    [DataMember(Name = "areaM2")]
    public double AreaM2 { get; set; }

    [DataMember(Name = "volumeM3")]
    public double VolumeM3 { get; set; }

    [DataMember(Name = "boundaries")]
    public List<RoomBoundaryLoop> Boundaries { get; set; } = new();
}

[DataContract]
public sealed class RoomBoundaryLoop
{
    [DataMember(Name = "segments")]
    public List<RoomBoundarySegment> Segments { get; set; } = new();
}

[DataContract]
public sealed class RoomBoundarySegment
{
    [DataMember(Name = "elementId")]
    public long ElementId { get; set; }

    [DataMember(Name = "lengthMm")]
    public double LengthMm { get; set; }

    [DataMember(Name = "startMm")]
    public VectorSnapshot StartMm { get; set; } = new();

    [DataMember(Name = "endMm")]
    public VectorSnapshot EndMm { get; set; } = new();
}
