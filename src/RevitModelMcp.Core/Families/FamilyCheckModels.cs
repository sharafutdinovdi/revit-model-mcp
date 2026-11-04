using System.Runtime.Serialization;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Families;

[DataContract]
public sealed record FamilyCheckOptions
{
    [OnDeserializing]
    private void SetDefaults(StreamingContext context)
    {
        Flex = true;
        HostTest = true;
        Previews = ["3d", "plan", "front"];
        DetailLevels = ["coarse", "fine"];
        MaxTypes = 10;
        ImagePx = 800;
    }

    [DataMember(Name = "types")] public List<string>? Types { get; set; } = null;
    [DataMember(Name = "flex")] public bool Flex { get; set; } = true;
    [DataMember(Name = "variants")] public List<FamilyCheckVariant>? Variants { get; set; } = null;
    [DataMember(Name = "expected")] public Dictionary<string, FamilyCheckExpected>? Expected { get; set; } = null;
    [DataMember(Name = "previews")] public List<string> Previews { get; set; } = ["3d", "plan", "front"];
    [DataMember(Name = "detailLevels")] public List<string> DetailLevels { get; set; } = ["coarse", "fine"];
    [DataMember(Name = "hostTest")] public bool HostTest { get; set; } = true;
    [DataMember(Name = "maxTypes")] public int MaxTypes { get; set; } = 10;
    [DataMember(Name = "imagePx")] public int ImagePx { get; set; } = 800;
}

[DataContract]
public sealed record FamilyCheckVariant
{
    [DataMember(Name = "parameter")] public string Parameter { get; set; } = string.Empty;
    [DataMember(Name = "values_mm")] public List<double> ValuesMm { get; set; } = [];
}

[DataContract]
public sealed record FamilyCheckExpected
{
    [OnDeserializing]
    private void SetDefaults(StreamingContext context) => ToleranceMm = 2;

    [DataMember(Name = "width_mm")] public double? WidthMm { get; set; } = null;
    [DataMember(Name = "depth_mm")] public double? DepthMm { get; set; } = null;
    [DataMember(Name = "height_mm")] public double? HeightMm { get; set; } = null;
    [DataMember(Name = "tolerance_mm")] public double ToleranceMm { get; set; } = 2;
}

[DataContract]
public sealed record FamilyCheckBounds
{
    [DataMember(Name = "width")] public double Width { get; set; } = 0;
    [DataMember(Name = "depth")] public double Depth { get; set; } = 0;
    [DataMember(Name = "height")] public double Height { get; set; } = 0;
}

[DataContract]
public sealed record FamilyCheckFinding
{
    [DataMember(Name = "id")] public string Id { get; set; } = string.Empty;
    [DataMember(Name = "severity")] public string Severity { get; set; } = "info";
    [DataMember(Name = "type")] public string? Type { get; set; } = null;
    [DataMember(Name = "message")] public string Message { get; set; } = string.Empty;
    [DataMember(Name = "evidence")] public Dictionary<string, string> Evidence { get; set; } = [];
}

[DataContract]
public sealed record FamilyCheckData
{
    [DataMember(Name = "mode")] public string Mode { get; set; } = string.Empty;
    [DataMember(Name = "families")] public List<FamilyCheckFamily> Families { get; set; } = [];
    [DataMember(Name = "skipped")] public List<SkippedRead> Skipped { get; set; } = [];
    [DataMember(Name = "skippedCount")] public int SkippedCount { get; set; } = 0;
    [DataMember(Name = "summary")] public Dictionary<string, int> Summary { get; set; } = [];
}

[DataContract]
public sealed record FamilyCheckFamily
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "category")] public string? Category { get; set; } = null;
    [DataMember(Name = "status")] public string Status { get; set; } = "not_assessed";
    [DataMember(Name = "reason")] public string? Reason { get; set; } = null;
    [DataMember(Name = "types")] public List<FamilyCheckType> Types { get; set; } = [];
    [DataMember(Name = "checks")] public List<FamilyCheckFinding> Checks { get; set; } = [];
    [DataMember(Name = "previews")] public List<FamilyCheckPreview> Previews { get; set; } = [];
    [DataMember(Name = "structure")] public Dictionary<string, int> Structure { get; set; } = [];
}

[DataContract]
public sealed record FamilyCheckType
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "status")] public string Status { get; set; } = "not_assessed";
    [DataMember(Name = "variants")] public List<FamilyCheckVariantResult> Variants { get; set; } = [];
    [DataMember(Name = "placed")] public bool? Placed { get; set; } = null;
    [DataMember(Name = "hostCut")] public bool? HostCut { get; set; } = null;
}

[DataContract]
public sealed record FamilyCheckVariantResult
{
    [DataMember(Name = "parameter")] public string Parameter { get; set; } = string.Empty;
    [DataMember(Name = "value_mm")] public double ValueMm { get; set; } = 0;
    [DataMember(Name = "status")] public string Status { get; set; } = "not_assessed";
    [DataMember(Name = "bboxMm")] public FamilyCheckBounds? BboxMm { get; set; } = null;
    [DataMember(Name = "volumeM3")] public double? VolumeM3 { get; set; } = null;
}

[DataContract]
public sealed record FamilyCheckPreview
{
    [DataMember(Name = "type")] public string Type { get; set; } = string.Empty;
    [DataMember(Name = "view")] public string View { get; set; } = string.Empty;
    [DataMember(Name = "detail")] public string Detail { get; set; } = string.Empty;
    [DataMember(Name = "path")] public string? Path { get; set; } = null;
    [DataMember(Name = "artifactName")] public string ArtifactName { get; set; } = string.Empty;
    [DataMember(Name = "artifact")] public string Artifact { get; set; } = string.Empty;
    [DataMember(Name = "widthPx")] public int WidthPx { get; set; } = 0;
    [DataMember(Name = "heightPx")] public int HeightPx { get; set; } = 0;
}
