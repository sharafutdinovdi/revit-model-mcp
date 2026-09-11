using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed class ViewDumpReport
{
    [DataMember(Name = "command", Order = 1)]
    public string Command { get; set; } = "views-dump";

    [DataMember(Name = "status", Order = 2)]
    public string Status { get; set; } = "partial";

    [DataMember(Name = "message", Order = 3, EmitDefaultValue = false)]
    public string? Message { get; set; }

    [DataMember(Name = "startedAt", Order = 4)]
    public string? StartedAt { get; set; }

    [DataMember(Name = "updatedAt", Order = 5)]
    public string? UpdatedAt { get; set; }

    [DataMember(Name = "completedAt", Order = 6, EmitDefaultValue = false)]
    public string? CompletedAt { get; set; }

    [DataMember(Name = "documentTitle", Order = 7, EmitDefaultValue = false)]
    public string? DocumentTitle { get; set; }

    [DataMember(Name = "responder", Order = 8)]
    public ResponderInfo Responder { get; set; } = new();

    [DataMember(Name = "originalViewName", Order = 9, EmitDefaultValue = false)]
    public string? OriginalViewName { get; set; }

    [DataMember(Name = "originalViewRestored", Order = 10, EmitDefaultValue = false)]
    public bool? OriginalViewRestored { get; set; }

    [DataMember(Name = "processedElements", Order = 11)]
    public int ProcessedElements { get; set; }

    [DataMember(Name = "totalElements", Order = 12)]
    public int TotalElements { get; set; }

    [DataMember(Name = "rejectedJobsWhileBusy", Order = 13)]
    public int RejectedJobsWhileBusy { get; set; }

    [DataMember(Name = "openedViews", Order = 14)]
    public List<string> OpenedViews { get; set; } = new();

    [DataMember(Name = "closedViews", Order = 15)]
    public List<string> ClosedViews { get; set; } = new();

    [DataMember(Name = "views", Order = 16)]
    public List<ViewDumpView> Views { get; set; } = new();
}

[DataContract]
public sealed class ViewDumpView
{
    [DataMember(Name = "requestedName", Order = 1)]
    public string RequestedName { get; set; } = string.Empty;

    [DataMember(Name = "status", Order = 2)]
    public string Status { get; set; } = "pending";

    [DataMember(Name = "error", Order = 3, EmitDefaultValue = false)]
    public string? Error { get; set; }

    [DataMember(Name = "header", Order = 4, EmitDefaultValue = false)]
    public ViewDumpHeader? Header { get; set; }

    [DataMember(Name = "categories", Order = 5)]
    public List<ViewCategorySummary> Categories { get; set; } = new();

    [DataMember(Name = "elements", Order = 6)]
    public List<ViewElementDump> Elements { get; set; } = new();

    public static ViewDumpView Missing(string requestedName)
    {
        return new ViewDumpView
        {
            RequestedName = requestedName,
            Status = "not-found",
            Error = $"Вид «{requestedName}» не найден."
        };
    }
}

[DataContract]
public sealed class ViewDumpHeader
{
    [DataMember(Name = "name")]
    public string? Name { get; set; }

    [DataMember(Name = "type")]
    public string? Type { get; set; }

    [DataMember(Name = "level", EmitDefaultValue = false)]
    public string? Level { get; set; }

    [DataMember(Name = "scale")]
    public int Scale { get; set; }

    [DataMember(Name = "template", EmitDefaultValue = false)]
    public string? Template { get; set; }

    [DataMember(Name = "discipline", EmitDefaultValue = false)]
    public string? Discipline { get; set; }

    [DataMember(Name = "filterCount")]
    public int FilterCount { get; set; }

    [DataMember(Name = "graphicOverrideCount")]
    public int GraphicOverrideCount { get; set; }

    [DataMember(Name = "elementCount")]
    public int ElementCount { get; set; }
}

[DataContract]
public sealed class ViewCategorySummary
{
    [DataMember(Name = "category")]
    public string Category { get; set; } = string.Empty;

    [DataMember(Name = "count")]
    public int Count { get; set; }

    [DataMember(Name = "differentTypes")]
    public int DifferentTypes { get; set; }
}

[DataContract]
public sealed class ViewElementDump
{
    [DataMember(Name = "id", Order = 1)]
    public long Id { get; set; }

    [DataMember(Name = "category", Order = 2, EmitDefaultValue = false)]
    public string? Category { get; set; }

    [DataMember(Name = "family", Order = 3, EmitDefaultValue = false)]
    public string? Family { get; set; }

    [DataMember(Name = "type", Order = 4, EmitDefaultValue = false)]
    public string? Type { get; set; }

    [DataMember(Name = "name", Order = 5, EmitDefaultValue = false)]
    public string? Name { get; set; }

    [DataMember(Name = "level", Order = 6, EmitDefaultValue = false)]
    public string? Level { get; set; }

    [DataMember(Name = "workset", Order = 7, EmitDefaultValue = false)]
    public string? Workset { get; set; }

    [DataMember(Name = "phase", Order = 8, EmitDefaultValue = false)]
    public string? Phase { get; set; }

    [DataMember(Name = "lengthMm", Order = 9, EmitDefaultValue = false)]
    public double? LengthMm { get; set; }

    [DataMember(Name = "thicknessMm", Order = 10, EmitDefaultValue = false)]
    public double? ThicknessMm { get; set; }

    [DataMember(Name = "areaM2", Order = 11, EmitDefaultValue = false)]
    public double? AreaM2 { get; set; }

    [DataMember(Name = "volumeM3", Order = 12, EmitDefaultValue = false)]
    public double? VolumeM3 { get; set; }

    [DataMember(Name = "profileParameters", Order = 13)]
    public Dictionary<string, string> ProfileParameters { get; set; } = new();

    [DataMember(Name = "hasWarnings", Order = 14)]
    public bool HasWarnings { get; set; }
}
