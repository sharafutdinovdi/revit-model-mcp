using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed class CommandResponse<T>
{
    [DataMember(Name = "command", Order = 1)]
    public string Command { get; set; } = string.Empty;

    [DataMember(Name = "success", Order = 2)]
    public bool Success { get; set; }

    [DataMember(Name = "partial", Order = 3)]
    public bool Partial { get; set; }

    [DataMember(Name = "data", Order = 4, EmitDefaultValue = false)]
    public T? Data { get; set; }

    [DataMember(Name = "message", Order = 5, EmitDefaultValue = false)]
    public string? Message { get; set; }

    [DataMember(Name = "elapsedMs", Order = 6)]
    public long ElapsedMs { get; set; }

    [DataMember(Name = "responder", Order = 7)]
    public ResponderInfo Responder { get; set; } = new();

    [DataMember(Name = "error", Order = 8, EmitDefaultValue = false)]
    public string? Error { get; set; }

    [DataMember(Name = "activeView", Order = 9, EmitDefaultValue = false)]
    public string? ActiveView { get; set; }

    public static CommandResponse<T> Ok(string command, T data, long elapsedMs, string? message = null)
    {
        return new CommandResponse<T>
        {
            Command = command,
            Success = true,
            Data = data,
            Message = message,
            ElapsedMs = elapsedMs
        };
    }

    public static CommandResponse<T> PartialResult(
        string command,
        T data,
        string message,
        long elapsedMs)
    {
        return new CommandResponse<T>
        {
            Command = command,
            Success = false,
            Partial = true,
            Data = data,
            Message = message,
            ElapsedMs = elapsedMs
        };
    }

    public static CommandResponse<T> Fail(string command, string message, long elapsedMs)
    {
        return new CommandResponse<T>
        {
            Command = command,
            Success = false,
            Message = message,
            ElapsedMs = elapsedMs
        };
    }

    public static CommandResponse<T> ViewNotFound(string command, string view, long elapsedMs)
    {
        return Fail(command, $"Вид «{view}» не найден. Посмотрите допустимые виды через list-views.", elapsedMs);
    }

    public static CommandResponse<T> ElementNotFound(string command, long id, long elapsedMs)
    {
        return Fail(command, $"Элемент с id {id} не найден.", elapsedMs);
    }
}

public static class ReadCommandResponseFactory
{
    public static CommandResponse<string> Ping(long elapsedMs)
    {
        return CommandResponse<string>.Ok("ping", "pong", elapsedMs);
    }
}

[DataContract]
public sealed class DocumentInfoData
{
    [DataMember(Name = "fileName")]
    public string FileName { get; set; } = string.Empty;

    [DataMember(Name = "revitVersion")]
    public string RevitVersion { get; set; } = string.Empty;

    [DataMember(Name = "isWorkshared")]
    public bool IsWorkshared { get; set; }

    [DataMember(Name = "levels")]
    public List<DocumentLevelInfo> Levels { get; set; } = new();

    [DataMember(Name = "areaSchemes")]
    public List<DocumentAreaSchemeInfo> AreaSchemes { get; set; } = new();

    [DataMember(Name = "worksets")]
    public List<DocumentWorksetInfo> Worksets { get; set; } = new();

    [DataMember(Name = "viewCount")]
    public int ViewCount { get; set; }
}

[DataContract]
public sealed class DocumentLevelInfo
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "elevationMm")]
    public double ElevationMm { get; set; }

    [DataMember(Name = "roomCount")]
    public int RoomCount { get; set; }
}

[DataContract]
public sealed class DocumentAreaSchemeInfo
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "isGrossBuildingArea")]
    public bool IsGrossBuildingArea { get; set; }

    [DataMember(Name = "areaCount")]
    public int AreaCount { get; set; }
}

[DataContract]
public sealed class DocumentWorksetInfo
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "isOpen")]
    public bool IsOpen { get; set; }
}

[DataContract]
public sealed class ViewListData
{
    [DataMember(Name = "elementPresenceMethod")]
    public string ElementPresenceMethod { get; set; } = "not-read";

    [DataMember(Name = "processed")]
    public int Processed { get; set; }

    [DataMember(Name = "total")]
    public int Total { get; set; }

    [DataMember(Name = "views")]
    public List<ViewListItem> Views { get; set; } = new();
}

[DataContract]
public sealed class ViewListItem
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "type")]
    public string Type { get; set; } = string.Empty;

    [DataMember(Name = "level", EmitDefaultValue = false)]
    public string? Level { get; set; }

    [DataMember(Name = "scale")]
    public int Scale { get; set; }

    [DataMember(Name = "template", EmitDefaultValue = false)]
    public string? Template { get; set; }
}

[DataContract]
public sealed class ViewExportData
{
    [DataMember(Name = "fileName")]
    public string FileName { get; set; } = string.Empty;

    [DataMember(Name = "width")]
    public int Width { get; set; }

    [DataMember(Name = "height")]
    public int Height { get; set; }

    [DataMember(Name = "sizeBytes")]
    public long SizeBytes { get; set; }

    [DataMember(Name = "viewName")]
    public string ViewName { get; set; } = string.Empty;

    [DataMember(Name = "viewType")]
    public string ViewType { get; set; } = string.Empty;
}

[DataContract]
public sealed class ViewSummaryData
{
    [DataMember(Name = "header")]
    public ViewDumpHeader Header { get; set; } = new();

    [DataMember(Name = "categories")]
    public List<ViewCategorySummary> Categories { get; set; } = new();
}

[DataContract]
public sealed class ViewElementsData
{
    [DataMember(Name = "view")]
    public string View { get; set; } = string.Empty;

    [DataMember(Name = "categories")]
    public List<string> Categories { get; set; } = new();

    [DataMember(Name = "offset")]
    public int Offset { get; set; }

    [DataMember(Name = "limit")]
    public int Limit { get; set; }

    [DataMember(Name = "total")]
    public int Total { get; set; }

    [DataMember(Name = "hasMore")]
    public bool HasMore { get; set; }

    [DataMember(Name = "processed")]
    public int Processed { get; set; }

    [DataMember(Name = "elements")]
    public List<ViewElementDump> Elements { get; set; } = new();

    [DataMember(Name = "rejectedJobsWhileBusy")]
    public int RejectedJobsWhileBusy { get; set; }
}

[DataContract]
public sealed class ElementDetailsData
{
    [DataMember(Name = "element")]
    public ViewElementDump Element { get; set; } = new();

    [DataMember(Name = "parameters")]
    public List<ElementParameterDetail> Parameters { get; set; } = new();

    [DataMember(Name = "typeElement", EmitDefaultValue = false)]
    public ElementTypeDetails? TypeElement { get; set; }

    [DataMember(Name = "warnings")]
    public List<ElementWarningInfo> Warnings { get; set; } = new();

    [DataMember(Name = "room", EmitDefaultValue = false)]
    public RoomDetails? Room { get; set; }
}

[DataContract]
public sealed class ElementWarningInfo
{
    [DataMember(Name = "text")]
    public string Text { get; set; } = string.Empty;

    [DataMember(Name = "severity")]
    public string Severity { get; set; } = string.Empty;
}

[DataContract]
public sealed class ElementTypeDetails
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "family", EmitDefaultValue = false)]
    public string? Family { get; set; }

    [DataMember(Name = "name", EmitDefaultValue = false)]
    public string? Name { get; set; }

    [DataMember(Name = "parameters")]
    public List<ElementParameterDetail> Parameters { get; set; } = new();
}

[DataContract]
public sealed class ElementParameterDetail
{
    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "storageType")]
    public string StorageType { get; set; } = string.Empty;

    [DataMember(Name = "hasValue")]
    public bool HasValue { get; set; }

    [DataMember(Name = "value", EmitDefaultValue = false)]
    public string? Value { get; set; }

    [DataMember(Name = "internalValue", EmitDefaultValue = false)]
    public string? InternalValue { get; set; }

    [DataMember(Name = "metricValue", EmitDefaultValue = false)]
    public double? MetricValue { get; set; }

    [DataMember(Name = "metricUnit", EmitDefaultValue = false)]
    public string? MetricUnit { get; set; }
}

[DataContract]
public sealed class ViewWarningsData
{
    [DataMember(Name = "view")]
    public string View { get; set; } = string.Empty;

    [DataMember(Name = "scope")]
    public string Scope { get; set; } = "view-elements";

    [DataMember(Name = "matchingNote")]
    public string MatchingNote { get; set; } = string.Empty;

    [DataMember(Name = "warnings")]
    public List<ViewWarningInfo> Warnings { get; set; } = new();
}

[DataContract]
public sealed class ViewWarningInfo
{
    [DataMember(Name = "text")]
    public string Text { get; set; } = string.Empty;

    [DataMember(Name = "severity")]
    public string Severity { get; set; } = string.Empty;

    [DataMember(Name = "hasElementsOnView")]
    public bool HasElementsOnView { get; set; }

    [DataMember(Name = "elements")]
    public List<ViewWarningElementInfo> Elements { get; set; } = new();
}

[DataContract]
public sealed class ViewWarningElementInfo
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "presentOnView")]
    public bool PresentOnView { get; set; }
}

public static class PageSlice
{
    public static (IReadOnlyList<T> Items, bool HasMore) Create<T>(IReadOnlyList<T> items, int offset, int limit)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        if (offset < 0 || limit <= 0)
        {
            throw new ArgumentOutOfRangeException(offset < 0 ? nameof(offset) : nameof(limit));
        }

        var page = items.Skip(offset).Take(limit).ToList();
        var hasMore = (long)offset + page.Count < items.Count;
        return (page, hasMore);
    }
}
