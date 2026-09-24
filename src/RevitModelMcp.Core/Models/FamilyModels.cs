using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed class FamilyAuditData
{
    [DataMember(Name = "mode")] public string Mode { get; set; } = string.Empty;
    [DataMember(Name = "families")] public List<FamilyAuditFamily> Families { get; set; } = [];
}

[DataContract]
public sealed class FamilyAuditFamily
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "status")] public string Status { get; set; } = "audited";
    [DataMember(Name = "reason", EmitDefaultValue = false)] public string? Reason { get; set; }
    [DataMember(Name = "category", EmitDefaultValue = false)] public string? Category { get; set; }
    [DataMember(Name = "isShared")] public bool IsShared { get; set; }
    [DataMember(Name = "sharedFlagEditable")] public bool SharedFlagEditable { get; set; }
    [DataMember(Name = "parameters")] public List<FamilyParameterData> Parameters { get; set; } = [];
    [DataMember(Name = "purgeable")] public Dictionary<string, int> Purgeable { get; set; } = [];
    [DataMember(Name = "purgeableTotal")] public int PurgeableTotal { get; set; }
    [DataMember(Name = "purgeCoverage")] public string PurgeCoverage { get; set; } = string.Empty;
}

[DataContract]
public sealed class FamilyParameterData
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "isInstance")] public bool IsInstance { get; set; }
    [DataMember(Name = "isShared")] public bool IsShared { get; set; }
    [DataMember(Name = "guid", EmitDefaultValue = false)] public string? Guid { get; set; }
    [DataMember(Name = "group")] public string Group { get; set; } = string.Empty;
    [DataMember(Name = "formula", EmitDefaultValue = false)] public string? Formula { get; set; }
    [DataMember(Name = "isReporting")] public bool IsReporting { get; set; }
    [DataMember(Name = "used")] public bool Used { get; set; }
    [DataMember(Name = "usedBy")] public List<string> UsedBy { get; set; } = [];
    [DataMember(Name = "builtIn")] public bool BuiltIn { get; set; }
    [DataMember(Name = "dataCarrierRisk", EmitDefaultValue = false)] public bool DataCarrierRisk { get; set; }
}

[DataContract]
public sealed class FamilyEditData
{
    [DataMember(Name = "mode")] public string Mode { get; set; } = string.Empty;
    [DataMember(Name = "dryRun")] public bool DryRun { get; set; }
    [DataMember(Name = "committed")] public bool Committed { get; set; }
    [DataMember(Name = "rolledBack", EmitDefaultValue = false)] public bool RolledBack { get; set; }
    [DataMember(Name = "failedFamily")] public string? FailedFamily { get; set; }
    [DataMember(Name = "families")] public List<FamilyEditFamily> Families { get; set; } = [];
    [DataMember(Name = "summary", EmitDefaultValue = false)] public string? Summary { get; set; }
    [DataMember(Name = "undoName", EmitDefaultValue = false)] public string? UndoName { get; set; }
}

[DataContract]
public sealed class FamilyEditFamily
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "status")] public string Status { get; set; } = string.Empty;
    [DataMember(Name = "reason", EmitDefaultValue = false)] public string? Reason { get; set; }
    [DataMember(Name = "loaded")] public bool Loaded { get; set; }
    [DataMember(Name = "rolledBack", EmitDefaultValue = false)] public bool RolledBack { get; set; }
    [DataMember(Name = "operations")] public List<FamilyOperationResult> Operations { get; set; } = [];
}

[DataContract]
public sealed class FamilyOperationResult
{
    [DataMember(Name = "op")] public string Op { get; set; } = string.Empty;
    [DataMember(Name = "results", EmitDefaultValue = false)] public List<FamilyItemResult>? Results { get; set; }
    [DataMember(Name = "status", EmitDefaultValue = false)] public string? Status { get; set; }
    [DataMember(Name = "before", EmitDefaultValue = false)] public bool? Before { get; set; }
    [DataMember(Name = "after", EmitDefaultValue = false)] public bool? After { get; set; }
    [DataMember(Name = "passes", EmitDefaultValue = false)] public int? Passes { get; set; }
    [DataMember(Name = "coverage", EmitDefaultValue = false)] public string? Coverage { get; set; }
    [DataMember(Name = "deleted", EmitDefaultValue = false)] public Dictionary<string, int>? Deleted { get; set; }
}

[DataContract]
public sealed class FamilyItemResult
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "status")] public string Status { get; set; } = string.Empty;
    [DataMember(Name = "guid", EmitDefaultValue = false)] public string? Guid { get; set; }
    [DataMember(Name = "reason", EmitDefaultValue = false)] public string? Reason { get; set; }
    [DataMember(Name = "usedBy", EmitDefaultValue = false)] public List<string>? UsedBy { get; set; }
}
