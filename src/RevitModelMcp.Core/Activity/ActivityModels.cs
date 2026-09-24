using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Activity;

/// <summary>Lifecycle state of one recorded MCP job, shown as a status icon in the activity pane.</summary>
public enum ActivityState
{
    Queued,
    Running,
    Done,
    Failed,
    DryRun
}

/// <summary>One element touched by an action, as shown in an expanded activity row.</summary>
[DataContract]
public sealed class ActivityElementRef
{
    [DataMember(Name = "id")] public long Id { get; set; }
    [DataMember(Name = "category", EmitDefaultValue = false)] public string? Category { get; set; }
    [DataMember(Name = "name", EmitDefaultValue = false)] public string? Name { get; set; }
}

/// <summary>One entry in the in-memory activity ring buffer and the <c>activity.log</c> JSON-lines file.</summary>
[DataContract]
public sealed class ActivityEntry
{
    [DataMember(Name = "id")] public string Id { get; set; } = string.Empty;
    [DataMember(Name = "time")] public DateTimeOffset Time { get; set; }
    [DataMember(Name = "clientName")] public string ClientName { get; set; } = "unknown";
    [DataMember(Name = "command")] public string Command { get; set; } = string.Empty;
    [DataMember(Name = "document")] public string Document { get; set; } = string.Empty;
    [DataMember(Name = "state")] public string State { get; set; } = "done";
    [DataMember(Name = "summary")] public string Summary { get; set; } = string.Empty;
    [DataMember(Name = "changed")] public List<ActivityElementRef> Changed { get; set; } = [];
    [DataMember(Name = "created")] public List<ActivityElementRef> Created { get; set; } = [];
    [DataMember(Name = "deleted")] public List<ActivityElementRef> Deleted { get; set; } = [];

    /// <summary>True number of changed elements; <see cref="Changed"/> holds at most the first 5000.</summary>
    [DataMember(Name = "changedTotal")] public int ChangedTotal { get; set; }
    [DataMember(Name = "createdTotal")] public int CreatedTotal { get; set; }
    [DataMember(Name = "deletedTotal")] public int DeletedTotal { get; set; }

    /// <summary>
    /// Elements the action itself targeted (for example the ids a <c>move</c> job was asked to move),
    /// distinct from <see cref="ChangedCount"/>/<see cref="CreatedCount"/>/<see cref="DeletedCount"/>,
    /// which also include dependents Revit touches as a side effect. Drives the row title; null when the
    /// command has no well-defined target count (dry runs still populate it the same as a real run).
    /// </summary>
    [DataMember(Name = "actionCount", EmitDefaultValue = false)] public int? ActionCount { get; set; }

    public int ChangedCount => Math.Max(ChangedTotal, Changed.Count);
    public int CreatedCount => Math.Max(CreatedTotal, Created.Count);
    public int DeletedCount => Math.Max(DeletedTotal, Deleted.Count);

    [DataMember(Name = "undoEntryName", EmitDefaultValue = false)] public string? UndoEntryName { get; set; }
    [DataMember(Name = "dryRun")] public bool DryRun { get; set; }
    [DataMember(Name = "undone")] public bool Undone { get; set; }
}
