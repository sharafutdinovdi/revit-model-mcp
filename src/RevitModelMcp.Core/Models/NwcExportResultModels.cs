using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed class NwcViewResult
{
    [DataMember(Name = "id")] public long Id { get; set; }
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
}

[DataContract]
public sealed class NwcPathChecks
{
    [DataMember(Name = "parentExists")] public bool ParentExists { get; set; }
    [DataMember(Name = "targetExists")] public bool TargetExists { get; set; }
}

[DataContract]
public sealed class NwcOptionsResult
{
    [DataMember(Name = "scope")] public string Scope { get; set; } = string.Empty;
    [DataMember(Name = "view")] public string? View { get; set; }
    [DataMember(Name = "elementIds")] public List<long>? ElementIds { get; set; }
    [DataMember(Name = "coordinates")] public string Coordinates { get; set; } = string.Empty;
    [DataMember(Name = "parameters")] public string Parameters { get; set; } = string.Empty;
    [DataMember(Name = "exportElementIds")] public bool ExportElementIds { get; set; }
    [DataMember(Name = "convertElementProperties")] public bool ConvertElementProperties { get; set; }
    [DataMember(Name = "exportParts")] public bool ExportParts { get; set; }
    [DataMember(Name = "exportRoomAsAttribute")] public bool ExportRoomAsAttribute { get; set; }
    [DataMember(Name = "exportRoomGeometry")] public bool ExportRoomGeometry { get; set; }
    [DataMember(Name = "convertLights")] public bool ConvertLights { get; set; }
    [DataMember(Name = "convertLinkedCadFormats")] public bool ConvertLinkedCadFormats { get; set; }
    [DataMember(Name = "exportLinks")] public bool ExportLinks { get; set; }
    [DataMember(Name = "exportUrls")] public bool ExportUrls { get; set; }
    [DataMember(Name = "divideFileIntoLevels")] public bool DivideFileIntoLevels { get; set; }
    [DataMember(Name = "findMissingMaterials")] public bool FindMissingMaterials { get; set; }
    [DataMember(Name = "facetingFactor")] public double FacetingFactor { get; set; }
    [DataMember(Name = "sources")] public Dictionary<string, string> Sources { get; set; } = [];
}
