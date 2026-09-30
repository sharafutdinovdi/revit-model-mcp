using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed record ModelSnapshotParameterRule
{
    [DataMember(Name = "category")] public string Category { get; set; } = string.Empty;
    [DataMember(Name = "parameter")] public string Parameter { get; set; } = string.Empty;
}

[DataContract]
public sealed record ModelSnapshotData
{
    [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [DataMember(Name = "collectedAtUtc")] public string CollectedAtUtc { get; set; } = string.Empty;
    [DataMember(Name = "source")] public ModelSnapshotSource Source { get; set; } = new();
    [DataMember(Name = "passport")] public ModelSnapshotPassport Passport { get; set; } = new();
    [DataMember(Name = "warnings")] public ModelSnapshotWarnings Warnings { get; set; } = new();
    [DataMember(Name = "families")] public ModelSnapshotFamilies Families { get; set; } = new();
    [DataMember(Name = "parameterFill")] public ModelSnapshotParameterFill ParameterFill { get; set; } = new();
    [DataMember(Name = "skipped")] public List<SkippedRead> Skipped { get; set; } = [];
    [DataMember(Name = "skippedCount")] public int SkippedCount { get; set; }
}

[DataContract]
public sealed record ModelSnapshotSource
{
    [DataMember(Name = "path")] public string? Path { get; set; }
    [DataMember(Name = "kind")] public string Kind { get; set; } = "local";
    [DataMember(Name = "runtimeYear")] public int? RuntimeYear { get; set; }
    [DataMember(Name = "savedInYear")] public int? SavedInYear { get; set; }
    [DataMember(Name = "upgradedInMemory")] public bool UpgradedInMemory { get; set; }
}

[DataContract]
public sealed record ModelSnapshotPassport
{
    [DataMember(Name = "title")] public string Title { get; set; } = string.Empty;
    [DataMember(Name = "isWorkshared")] public bool IsWorkshared { get; set; }
    [DataMember(Name = "centralPath")] public string? CentralPath { get; set; }
    [DataMember(Name = "numberOfSaves")] public long? NumberOfSaves { get; set; }
    [DataMember(Name = "versionGuid")] public string? VersionGuid { get; set; }
    [DataMember(Name = "basicFileInfoUsername")] public string? BasicFileInfoUsername { get; set; }
    [DataMember(Name = "fileLastWriteUtc")] public string? FileLastWriteUtc { get; set; }
    [DataMember(Name = "fileSizeBytes")] public long? FileSizeBytes { get; set; }
    [DataMember(Name = "revitServer")] public object? RevitServer { get; set; }
    [DataMember(Name = "counts")] public Dictionary<string, int?> Counts { get; set; } = new();
    [DataMember(Name = "worksets")] public List<ModelSnapshotWorkset> Worksets { get; set; } = [];
}

[DataContract]
public sealed record ModelSnapshotWorkset
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "owner")] public string? Owner { get; set; }
    [DataMember(Name = "isOpen")] public bool IsOpen { get; set; }
}

[DataContract]
public sealed record ModelSnapshotWarnings
{
    [DataMember(Name = "total")] public int Total { get; set; }
    [DataMember(Name = "groups")] public List<ModelSnapshotWarningGroup> Groups { get; set; } = [];
}

[DataContract]
public sealed record ModelSnapshotWarningGroup
{
    [DataMember(Name = "text")] public string Text { get; set; } = string.Empty;
    [DataMember(Name = "count")] public int Count { get; set; }
    [DataMember(Name = "elementIds")] public List<long> ElementIds { get; set; } = [];
    [DataMember(Name = "elementIdsTruncated")] public bool ElementIdsTruncated { get; set; }

    public static ModelSnapshotWarningGroup Create(string text, int count, IEnumerable<long> affectedElementIds)
    {
        if (affectedElementIds is null)
            throw new ArgumentNullException(nameof(affectedElementIds));
        var distinctIds = new HashSet<long>();
        var elementIds = new List<long>();
        foreach (var id in affectedElementIds)
        {
            if (!distinctIds.Add(id)) continue;
            if (elementIds.Count == 200)
                return new ModelSnapshotWarningGroup
                {
                    Text = text,
                    Count = count,
                    ElementIds = elementIds,
                    ElementIdsTruncated = true
                };
            elementIds.Add(id);
        }
        return new ModelSnapshotWarningGroup { Text = text, Count = count, ElementIds = elementIds };
    }
}

[DataContract]
public sealed record ModelSnapshotFamilies
{
    [DataMember(Name = "total")] public int Total { get; set; }
    [DataMember(Name = "inPlace")] public int InPlace { get; set; }
    [DataMember(Name = "nonEditable")] public int NonEditable { get; set; }
    [DataMember(Name = "items")] public List<ModelSnapshotFamily> Items { get; set; } = [];
    [DataMember(Name = "signals")] public List<ModelSnapshotFamilySignal> Signals { get; set; } = [];
}

[DataContract]
public sealed record ModelSnapshotFamily
{
    [DataMember(Name = "name")] public string Name { get; set; } = string.Empty;
    [DataMember(Name = "category")] public string? Category { get; set; }
    [DataMember(Name = "isInPlace")] public bool IsInPlace { get; set; }
    [DataMember(Name = "isEditable")] public bool IsEditable { get; set; }
    [DataMember(Name = "typeCount")] public int TypeCount { get; set; }
    [DataMember(Name = "instanceCount")] public int InstanceCount { get; set; }
    [DataMember(Name = "warningCount")] public int WarningCount { get; set; }
}

[DataContract]
public sealed record ModelSnapshotFamilySignal
{
    [DataMember(Name = "family")] public string Family { get; set; } = string.Empty;
    [DataMember(Name = "signal")] public string Signal { get; set; } = string.Empty;
    [DataMember(Name = "detail")] public string Detail { get; set; } = string.Empty;
}

[DataContract]
public sealed record ModelSnapshotParameterFill
{
    [DataMember(Name = "rows")] public List<ModelSnapshotParameterRow> Rows { get; set; } = [];
}

[DataContract]
public sealed record ModelSnapshotParameterRow
{
    [DataMember(Name = "category")] public string Category { get; set; } = string.Empty;
    [DataMember(Name = "parameter")] public string Parameter { get; set; } = string.Empty;
    [DataMember(Name = "total")] public int Total { get; set; }
    [DataMember(Name = "filled")] public int Filled { get; set; }
    [DataMember(Name = "empty")] public int Empty { get; set; }
    [DataMember(Name = "missing")] public int Missing { get; set; }
    [DataMember(Name = "sampleEmptyIds")] public List<long> SampleEmptyIds { get; set; } = [];
}
