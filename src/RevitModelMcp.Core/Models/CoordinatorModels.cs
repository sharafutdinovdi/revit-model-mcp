using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed record ModelHealthData
{
    [DataMember(Name = "revitVersion")]
    public string RevitVersion { get; set; } = string.Empty;
    [DataMember(Name = "revitBuild")]
    public string RevitBuild { get; set; } = string.Empty;
    [DataMember(Name = "fileName")]
    public string FileName { get; set; } = string.Empty;
    [DataMember(Name = "isWorkshared")]
    public bool IsWorkshared { get; set; }
    [DataMember(Name = "fileSizeBytes")]
    public long? FileSizeBytes { get; set; }
    [DataMember(Name = "projectInfo")]
    public Dictionary<string, string?> ProjectInfo { get; set; } = new();
    [DataMember(Name = "counts")]
    public Dictionary<string, int?> Counts { get; set; } = new();
    [DataMember(Name = "topWarnings")]
    public List<HealthWarning> TopWarnings { get; set; } = new();
    [DataMember(Name = "units")]
    public Dictionary<string, string?> Units { get; set; } = new();
    [DataMember(Name = "skipped")]
    public List<SkippedMetric> Skipped { get; set; } = new();
}

[DataContract]
public sealed record HealthWarning
{
    [DataMember(Name = "text")]
    public string Text { get; set; } = string.Empty;
    [DataMember(Name = "count")]
    public int Count { get; set; }
}

[DataContract]
public sealed record SkippedMetric
{
    [DataMember(Name = "metric")]
    public string Metric { get; set; } = string.Empty;
    [DataMember(Name = "error")]
    public string Error { get; set; } = string.Empty;
}

[DataContract]
public sealed record LinksStatusData
{
    [DataMember(Name = "rvtLinks")]
    public List<RvtLinkStatus> RvtLinks { get; set; } = new();
    [DataMember(Name = "cadLinks")]
    public List<CadLinkStatus> CadLinks { get; set; } = new();
    [DataMember(Name = "images")]
    public List<ImageLinkStatus> Images { get; set; } = new();
    [DataMember(Name = "summary")]
    public LinkSummary Summary { get; set; } = new();
    [DataMember(Name = "listLimit")]
    public int ListLimit { get; set; }
}

[DataContract]
public sealed record RvtLinkStatus
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;
    [DataMember(Name = "typeId")]
    public long TypeId { get; set; }
    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;
    [DataMember(Name = "pathType")]
    public string PathType { get; set; } = string.Empty;
    [DataMember(Name = "path", EmitDefaultValue = false)]
    public string? Path { get; set; }
    [DataMember(Name = "instances")]
    public int Instances { get; set; }
    [DataMember(Name = "pinned")]
    public bool Pinned { get; set; }
    [DataMember(Name = "nested")]
    public bool Nested { get; set; }
    [DataMember(Name = "error", EmitDefaultValue = false)]
    public string? Error { get; set; }
}

[DataContract]
public sealed record CadLinkStatus
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;
    [DataMember(Name = "typeId")]
    public long TypeId { get; set; }
    [DataMember(Name = "isLinked")]
    public bool IsLinked { get; set; }
    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;
    [DataMember(Name = "path", EmitDefaultValue = false)]
    public string? Path { get; set; }
    [DataMember(Name = "instances")]
    public int Instances { get; set; }
    [DataMember(Name = "viewSpecific")]
    public bool ViewSpecific { get; set; }
    [DataMember(Name = "error", EmitDefaultValue = false)]
    public string? Error { get; set; }
}

[DataContract]
public sealed record ImageLinkStatus
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;
    [DataMember(Name = "typeId")]
    public long TypeId { get; set; }
    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;
    [DataMember(Name = "path", EmitDefaultValue = false)]
    public string? Path { get; set; }
    [DataMember(Name = "instances")]
    public int Instances { get; set; }
    [DataMember(Name = "error", EmitDefaultValue = false)]
    public string? Error { get; set; }
}

[DataContract]
public sealed record LinkSummary
{
    [DataMember(Name = "rvt")]
    public int Rvt { get; set; }
    [DataMember(Name = "rvtLoaded")]
    public int RvtLoaded { get; set; }
    [DataMember(Name = "cad")]
    public int Cad { get; set; }
    [DataMember(Name = "cadImports")]
    public int CadImports { get; set; }
    [DataMember(Name = "images")]
    public int Images { get; set; }
}

[DataContract]
public sealed record SharedCoordinatesData
{
    [DataMember(Name = "activeProjectLocation")]
    public string ActiveProjectLocation { get; set; } = string.Empty;
    [DataMember(Name = "projectLocations")]
    public List<string> ProjectLocations { get; set; } = new();
    [DataMember(Name = "projectBasePoint")]
    public CoordinatePoint ProjectBasePoint { get; set; } = new();
    [DataMember(Name = "surveyPoint")]
    public CoordinatePoint SurveyPoint { get; set; } = new();
    [DataMember(Name = "internalOriginToBasePointMm")]
    public CoordinateOffset InternalOriginToBasePointMm { get; set; } = new();
    [DataMember(Name = "trueNorthAngleDeg")]
    public double TrueNorthAngleDeg { get; set; }
    [DataMember(Name = "siteName")]
    public string SiteName { get; set; } = string.Empty;
    [DataMember(Name = "sharedSiteFromLinks")]
    public List<LinkSharedSite> SharedSiteFromLinks { get; set; } = new();
    [DataMember(Name = "listLimit")]
    public int ListLimit { get; set; }
    [DataMember(Name = "projectLocationsTotal")]
    public int ProjectLocationsTotal { get; set; }
    [DataMember(Name = "linkInstancesTotal")]
    public int LinkInstancesTotal { get; set; }
}

[DataContract]
public sealed record CoordinatePoint
{
    [DataMember(Name = "eastWestMm")]
    public double EastWestMm { get; set; }
    [DataMember(Name = "northSouthMm")]
    public double NorthSouthMm { get; set; }
    [DataMember(Name = "elevationMm")]
    public double ElevationMm { get; set; }
    [DataMember(Name = "angleToTrueNorthDeg", EmitDefaultValue = false)]
    public double? AngleToTrueNorthDeg { get; set; }
    [DataMember(Name = "clipped")]
    public bool? Clipped { get; set; }
}

[DataContract]
public sealed record CoordinateOffset
{
    [DataMember(Name = "x")]
    public double X { get; set; }
    [DataMember(Name = "y")]
    public double Y { get; set; }
    [DataMember(Name = "z")]
    public double Z { get; set; }
}

[DataContract]
public sealed record LinkSharedSite
{
    [DataMember(Name = "linkName")]
    public string LinkName { get; set; } = string.Empty;
    [DataMember(Name = "sharedSiteName")]
    public string? SharedSiteName { get; set; }
    [DataMember(Name = "hasOffset")]
    public bool HasOffset { get; set; }
    [DataMember(Name = "offsetMm")]
    public CoordinateOffset OffsetMm { get; set; } = new();
    [DataMember(Name = "rotationDeg")]
    public double RotationDeg { get; set; }
}

[DataContract]
public sealed record ParameterFillData
{
    [DataMember(Name = "scope")]
    public ParameterFillScope Scope { get; set; } = new();
    [DataMember(Name = "parameters")]
    public List<ParameterFillItem> Parameters { get; set; } = new();
}

[DataContract]
public sealed record ParameterFillScope
{
    [DataMember(Name = "categories")]
    public List<string> Categories { get; set; } = new();
    [DataMember(Name = "elements")]
    public int Elements { get; set; }
    [DataMember(Name = "level", EmitDefaultValue = false)]
    public string? Level { get; set; }
    [DataMember(Name = "workset", EmitDefaultValue = false)]
    public string? Workset { get; set; }
    [DataMember(Name = "view", EmitDefaultValue = false)]
    public string? View { get; set; }
}

[DataContract]
public sealed record ParameterFillItem
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;
    [DataMember(Name = "elements")]
    public int Elements { get; set; }
    [DataMember(Name = "filled")]
    public int Filled { get; set; }
    [DataMember(Name = "empty")]
    public int Empty { get; set; }
    [DataMember(Name = "missing")]
    public int Missing { get; set; }
    [DataMember(Name = "storageTypes")]
    public Dictionary<string, int> StorageTypes { get; set; } = new();
    [DataMember(Name = "owner")]
    public ParameterOwnerCounts Owner { get; set; } = new();
    [DataMember(Name = "emptySampleIds")]
    public List<long> EmptySampleIds { get; set; } = new();
    [DataMember(Name = "missingSampleIds")]
    public List<long> MissingSampleIds { get; set; } = new();
    [DataMember(Name = "byCategory")]
    public List<ParameterCategoryFill> ByCategory { get; set; } = new();
}

[DataContract]
public sealed record ParameterOwnerCounts
{
    [DataMember(Name = "instance")]
    public int Instance { get; set; }
    [DataMember(Name = "type")]
    public int Type { get; set; }
}

[DataContract]
public sealed record ParameterCategoryFill
{
    [DataMember(Name = "category")]
    public string Category { get; set; } = string.Empty;
    [DataMember(Name = "elements")]
    public int Elements { get; set; }
    [DataMember(Name = "filled")]
    public int Filled { get; set; }
    [DataMember(Name = "empty")]
    public int Empty { get; set; }
    [DataMember(Name = "missing")]
    public int Missing { get; set; }
}
