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
    [DataMember(Name = "element_ids")] public List<long>? ElementIds { get; set; }
    [DataMember(Name = "coordinates")] public string Coordinates { get; set; } = string.Empty;
    [DataMember(Name = "parameters")] public string Parameters { get; set; } = string.Empty;
    [DataMember(Name = "export_element_ids")] public bool ExportElementIds { get; set; }
    [DataMember(Name = "convert_element_properties")] public bool ConvertElementProperties { get; set; }
    [DataMember(Name = "export_parts")] public bool ExportParts { get; set; }
    [DataMember(Name = "export_room_as_attribute")] public bool ExportRoomAsAttribute { get; set; }
    [DataMember(Name = "export_room_geometry")] public bool ExportRoomGeometry { get; set; }
    [DataMember(Name = "convert_lights")] public bool ConvertLights { get; set; }
    [DataMember(Name = "convert_linked_cad_formats")] public bool ConvertLinkedCadFormats { get; set; }
    [DataMember(Name = "export_links")] public bool ExportLinks { get; set; }
    [DataMember(Name = "export_urls")] public bool ExportUrls { get; set; }
    [DataMember(Name = "divide_file_into_levels")] public bool DivideFileIntoLevels { get; set; }
    [DataMember(Name = "find_missing_materials")] public bool FindMissingMaterials { get; set; }
    [DataMember(Name = "faceting_factor")] public double FacetingFactor { get; set; }
}
