using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Control;

[DataContract]
public sealed class ActionVerification
{
    [DataMember(Name = "before", EmitDefaultValue = false)] public ActionFacts? Before { get; set; }
    [DataMember(Name = "after", EmitDefaultValue = false)] public ActionFacts? After { get; set; }
    [DataMember(Name = "changed", EmitDefaultValue = false)] public List<long>? Changed { get; set; }
    [DataMember(Name = "wouldCreate", EmitDefaultValue = false)] public bool? WouldCreate { get; set; }
}

[DataContract]
public sealed class ActionFacts
{
    [DataMember(Name = "elements", EmitDefaultValue = false)] public List<ActionFacts>? Elements { get; set; }
    [DataMember(Name = "id", EmitDefaultValue = false)] public long? Id { get; set; }
    [DataMember(Name = "category", EmitDefaultValue = false)] public string? Category { get; set; }
    [DataMember(Name = "boundingBoxMinMm", EmitDefaultValue = false)] public List<double>? BoundingBoxMinMm { get; set; }
    [DataMember(Name = "boundingBoxMaxMm", EmitDefaultValue = false)] public List<double>? BoundingBoxMaxMm { get; set; }
    [DataMember(Name = "family", EmitDefaultValue = false)] public string? Family { get; set; }
    [DataMember(Name = "type", EmitDefaultValue = false)] public string? Type { get; set; }
    [DataMember(Name = "level", EmitDefaultValue = false)] public string? Level { get; set; }
    [DataMember(Name = "parameter", EmitDefaultValue = false)] public string? Parameter { get; set; }
    [DataMember(Name = "value", EmitDefaultValue = false)] public string? Value { get; set; }
    [DataMember(Name = "storageType", EmitDefaultValue = false)] public string? StorageType { get; set; }
    [DataMember(Name = "owner", EmitDefaultValue = false)] public string? Owner { get; set; }
    [DataMember(Name = "requested", EmitDefaultValue = false)] public List<long>? Requested { get; set; }
    [DataMember(Name = "dependents", EmitDefaultValue = false)] public List<long>? Dependents { get; set; }
    [DataMember(Name = "stillPresent", EmitDefaultValue = false)] public List<long>? StillPresent { get; set; }
}

[DataContract]
public sealed class BatchStepResult
{
    [DataMember(Name = "index")] public int Index { get; set; }
    [DataMember(Name = "command")] public string Command { get; set; } = string.Empty;
    [DataMember(Name = "success")] public bool Success { get; set; }
    [DataMember(Name = "data", EmitDefaultValue = false)] public ActionResultData? Data { get; set; }
    [DataMember(Name = "error", EmitDefaultValue = false)] public string? Error { get; set; }
    [DataMember(Name = "rolledBack", EmitDefaultValue = false)] public bool? RolledBack { get; set; }
}
