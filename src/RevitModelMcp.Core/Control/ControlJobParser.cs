using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Control;

public enum ControlJobKind
{
    LegacySnapshot,
    ViewsDump,
    Ping,
    DocumentInfo,
    Documents,
    ModelHealth,
    LinksStatus,
    SharedCoordinates,
    ParameterFillCheck,
    ListViews,
    ViewSummary,
    ViewElements,
    ElementDetails,
    ViewWarnings,
    ExportView,
    QueryElements,
    AggregateElements,
    ListCatalog,
    ListWarnings,
    ListRelations,
    FamilyAudit,
    Action,
    Invalid,
    CompareLinkDatums,
    Jobs
}

public sealed class ControlJobParseResult
{
    private ControlJobParseResult(ControlJobKind kind, string command, string? error, Exception? cause = null)
    {
        Kind = kind;
        Command = command;
        Error = error;
        Cause = cause;
    }
    public ControlJobContract CoordinatorJob { get; internal set; } = new();
    public ControlJobKind Kind { get; }
    public string Command { get; }
    public string? CorrelationId { get; internal set; }
    public string? JobId { get; internal set; }
    public string ClientId { get; internal set; } = "unknown";
    public string ClientName { get; internal set; } = "unknown";
    public string? CancelJobId { get; internal set; }
    public IReadOnlyList<string> Views { get; internal set; } = Array.Empty<string>();
    public string? View { get; internal set; }
    public string? ViewType { get; internal set; }
    public string? NameContains { get; internal set; }
    public IReadOnlyList<string> Categories { get; internal set; } = Array.Empty<string>();
    public int Offset { get; internal set; }
    public int Limit { get; internal set; } = 100;
    public long? ElementId { get; internal set; }
    public ElementFilterSpec Filters { get; internal set; } = new();
    public IReadOnlyList<string> Fields { get; internal set; } = Array.Empty<string>();
    public QuerySortSpec Sort { get; internal set; } = new();
    public IReadOnlyList<string> GroupBy { get; internal set; } = Array.Empty<string>();
    public string? NumericField { get; internal set; }
    public string? CatalogSection { get; internal set; }
    public string? WarningText { get; internal set; }
    public bool IncludeGeometry { get; internal set; }
    public bool IncludeElements { get; internal set; }
    public string? Relation { get; internal set; }
    public long? SourceId { get; internal set; }
    public string? SourceName { get; internal set; }
    public int PixelSize { get; internal set; } = 1600;
    public bool ZoomToFit { get; internal set; } = true;
    public string? TargetDocument { get; internal set; }
    public int? TargetProcessId { get; internal set; }
    public ActionJobContract? Action { get; internal set; }
    public string? Error { get; }
    public Exception? Cause { get; }
    public static ControlJobParseResult LegacySnapshot()
    {
        return new ControlJobParseResult(ControlJobKind.LegacySnapshot, "legacy-snapshot", null);
    }
    public static ControlJobParseResult Invalid(string command, string error, Exception? cause = null)
    {
        return new ControlJobParseResult(ControlJobKind.Invalid, command, error, cause);
    }

    internal static ControlJobParseResult Create(ControlJobKind kind, string command)
    {
        return new ControlJobParseResult(kind, command, null);
    }

    private static ControlJobParseResult ViewsDump(IReadOnlyList<string> views)
    {
        var result = Create(ControlJobKind.ViewsDump, "views-dump");
        result.Views = views;
        return result;
    }

    private static ControlJobParseResult ViewCommand(ControlJobKind kind, string command, string view)
    {
        var result = Create(kind, command);
        result.View = view;
        return result;
    }

    private static ControlJobParseResult ListViews(string? viewType, string? nameContains)
    {
        var result = Create(ControlJobKind.ListViews, "list-views");
        result.ViewType = Normalize(viewType);
        result.NameContains = Normalize(nameContains);
        return result;
    }

    private static ControlJobParseResult ViewElements(
        string view,
        IReadOnlyList<string> categories,
        int offset,
        int limit)
    {
        var result = ViewCommand(ControlJobKind.ViewElements, "view-elements", view);
        result.Categories = categories;
        result.Offset = offset;
        result.Limit = limit;
        return result;
    }

    private static ControlJobParseResult ElementDetails(long elementId)
    {
        var result = Create(ControlJobKind.ElementDetails, "element-details");
        result.ElementId = elementId;
        return result;
    }

    private static ControlJobParseResult ExportView(string view, int pixelSize, bool zoomToFit)
    {
        var result = ViewCommand(ControlJobKind.ExportView, "export-view", view);
        result.PixelSize = pixelSize;
        result.ZoomToFit = zoomToFit;
        return result;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }
    public static ControlJobParseResult FromContract(ControlJobContract job)
    {
        var command = Normalize(job.Command);
        if (command is null)
        {
            return new ControlJobParseResult(ControlJobKind.Invalid, "invalid", "The command field is required.")
            {
                CorrelationId = job.CorrelationId,
                ClientId = string.IsNullOrWhiteSpace(job.ClientId) ? "unknown" : job.ClientId!,
                ClientName = string.IsNullOrWhiteSpace(job.ClientName) ? "unknown" : job.ClientName!
            };
        }

        var views = NormalizeMany(job.Views);
        var view = Normalize(job.View);
        var categories = NormalizeMany(job.Categories);
        var result = command switch
        {
            "views-dump" when views.Count == 0 => Invalid(command, "The views-dump command requires a non-empty views list."),
            "views-dump" => ViewsDump(views),
            "ping" => Create(ControlJobKind.Ping, command),
            "jobs" => Create(ControlJobKind.Jobs, command),
            "model-health" => Create(ControlJobKind.ModelHealth, command),
            "links-status" => Create(ControlJobKind.LinksStatus, command),
            "shared-coordinates" => Create(ControlJobKind.SharedCoordinates, command),
            "parameter-fill-check" => ParseParameterFill(job),
            "document-info" => Create(ControlJobKind.DocumentInfo, command),
            "documents" => Create(ControlJobKind.Documents, command),
            "list-views" => ListViews(job.ViewType, job.NameContains),
            "view-summary" => RequireView(ControlJobKind.ViewSummary, command, view),
            "view-elements" => ParseViewElements(command, view, categories, job.Offset, job.Limit),
            "element-details" => ParseElementDetails(command, job.Id),
            "view-warnings" => RequireView(ControlJobKind.ViewWarnings, command, view),
            "export-view" => ParseExportView(command, view, job.PixelSize, job.ZoomToFit),
            "query-elements" => UniversalJobParser.ParseQuery(job),
            "aggregate-elements" => UniversalJobParser.ParseAggregate(job),
            "list-catalog" => UniversalJobParser.ParseCatalog(job),
            "list-warnings" => UniversalJobParser.ParseWarnings(job),
            "list-relations" => UniversalJobParser.ParseRelations(job),
            "family-audit" => ParseFamilyAudit(job),
            "compare-link-datums" => ParseCompareLinkDatums(job),
            _ when ActionJobParser.IsAction(command) => ActionJobParser.Parse(command, job),
            _ => Invalid(command, $"Unknown command: {command}.")
        };
        result.CorrelationId = job.CorrelationId;
        result.JobId = job.JobId;
        result.ClientId = string.IsNullOrWhiteSpace(job.ClientId) ? "unknown" : job.ClientId!;
        result.ClientName = string.IsNullOrWhiteSpace(job.ClientName) ? "unknown" : job.ClientName!;
        result.CancelJobId = job.CancelJobId;
        result.CoordinatorJob.CorrelationId = job.CorrelationId;
        if (command == "family-audit") result.CoordinatorJob = job;
        result.TargetDocument = command == "family-audit" ? null : Normalize(job.TargetDocument);
        result.TargetProcessId = job.TargetProcessId;
        return result;
    }

    private static ControlJobParseResult ParseFamilyAudit(ControlJobContract job)
    {
        var parsed = ActionJobParser.Parse("family-audit", job);
        if (parsed.Error is not null) return parsed;
        var result = ControlJobParseResult.Create(ControlJobKind.FamilyAudit, "family-audit");
        result.Action = parsed.Action;
        return result;
    }

    private static ControlJobParseResult ParseCompareLinkDatums(ControlJobContract job)
    {
        const string command = "compare-link-datums";
        try
        {
            var result = Create(ControlJobKind.CompareLinkDatums, command);
            result.Action = new ActionJobContract { DatumOptions = ActionJobParser.ParseDatumOptions(job) };
            return result;
        }
        catch (ArgumentException exception)
        {
            return Invalid(command, exception.Message);
        }
    }

    private static ControlJobParseResult ParseParameterFill(ControlJobContract job)
    {
        const string command = "parameter-fill-check";
        var categories = NormalizeMany(job.Categories);
        var parameters = NormalizeMany(job.Parameters);
        if (categories.Count is < 1 or > 20 || job.Categories?.Count > 20)
            return Invalid(command, "The categories list requires 1–20 names.");
        if (parameters.Count is < 1 or > 30 || job.Parameters?.Count > 30)
            return Invalid(command, "The parameters list requires 1–30 names.");
        if (job.SampleLimit is < 1 or > 100)
            return Invalid(command, "The sampleLimit must be between 1 and 100.");
        var query = UniversalJobParser.ParseQuery(job);
        if (query.Kind == ControlJobKind.Invalid) return Invalid(command, query.Error!);
        var result = Create(ControlJobKind.ParameterFillCheck, command);
        result.CoordinatorJob = new ControlJobContract
        {
            Command = command,
            Categories = categories.ToList(),
            Parameters = parameters.ToList(),
            Level = query.Filters.Level,
            Workset = query.Filters.Workset,
            View = query.Filters.View,
            SampleLimit = job.SampleLimit ?? 20,
            IncludeTypes = job.IncludeTypes ?? true
        };
        return result;
    }

    private static ControlJobParseResult RequireView(ControlJobKind kind, string command, string? view)
    {
        return view is null
            ? Invalid(command, $"The {command} command requires the view field.")
            : ViewCommand(kind, command, view);
    }

    private static ControlJobParseResult ParseViewElements(
        string command,
        string? view,
        IReadOnlyList<string> categories,
        int? offset,
        int? limit)
    {
        if (view is null)
        {
            return Invalid(command, "The view-elements command requires the view field.");
        }

        var resolvedOffset = offset ?? 0;
        var resolvedLimit = limit ?? 100;
        if (resolvedOffset < 0)
        {
            return Invalid(command, "The offset must not be negative.");
        }

        return resolvedLimit <= 0
            ? Invalid(command, "The limit must be greater than zero.")
            : ViewElements(view, categories, resolvedOffset, resolvedLimit);
    }

    private static ControlJobParseResult ParseElementDetails(string command, long? id)
    {
        return !id.HasValue || id.Value <= 0
            ? Invalid(command, "The element-details command requires a positive id.")
            : ElementDetails(id.Value);
    }

    private static ControlJobParseResult ParseExportView(
        string command,
        string? view,
        int? pixelSize,
        bool? zoomToFit)
    {
        if (view is null)
        {
            return Invalid(command, "The export-view command requires the view field.");
        }

        var resolvedPixelSize = pixelSize ?? 1600;
        return resolvedPixelSize is < 1 or > 4000
            ? Invalid(command, "The pixelSize must be between 1 and 4000 pixels.")
            : ExportView(view, resolvedPixelSize, zoomToFit ?? true);
    }

    private static IReadOnlyList<string> NormalizeMany(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public static class ControlJobParser
{
    public static ControlJobParseResult Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ControlJobParseResult.LegacySnapshot();
        }

        var isNwcExport = Regex.IsMatch(content, "\"command\"\\s*:\\s*\"export-nwc\"");
        try
        {
            if (isNwcExport)
                content = Regex.Replace(content, "\"parameters\"(?=\\s*:)", "\"nwcParameters\"");
            var serializer = new DataContractJsonSerializer(typeof(ControlJobContract),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            var job = serializer.ReadObject(stream) as ControlJobContract;
            return job is null
                ? ControlJobParseResult.Invalid("invalid", "The job JSON is empty.")
                : ControlJobParseResult.FromContract(job);
        }
        catch (Exception exception)
        {
            var result = ControlJobParseResult.Invalid(
                "invalid",
                isNwcExport ? "Failed to parse the export-nwc job JSON." : $"Failed to parse the job JSON: {exception.Message}",
                isNwcExport ? null : exception);
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                var envelope = new DataContractJsonSerializer(typeof(JobEnvelope)).ReadObject(stream) as JobEnvelope;
                if (envelope is not null)
                {
                    result = ControlJobParseResult.Invalid(envelope.Command ?? "invalid", result.Error!, isNwcExport ? null : exception);
                    result.CorrelationId = envelope.CorrelationId;
                }
            }
            catch (SerializationException)
            {
                // Malformed JSON has no recoverable job envelope.
            }
            return result;
        }
    }

    [DataContract]
    private sealed class JobEnvelope
    {
        [DataMember(Name = "command")]
        public string? Command { get; set; }

        [DataMember(Name = "correlationId")]
        public string? CorrelationId { get; set; }
    }
}

[DataContract]
public sealed partial class ControlJobContract
{
    [DataMember(Name = "jobId", EmitDefaultValue = false)]
    public string? JobId { get; set; }
    [DataMember(Name = "clientId", EmitDefaultValue = false)]
    public string? ClientId { get; set; }
    [DataMember(Name = "clientName", EmitDefaultValue = false)]
    public string? ClientName { get; set; }
    [DataMember(Name = "cancelJobId", EmitDefaultValue = false)]
    public string? CancelJobId { get; set; }
    [DataMember(Name = "correlationId", EmitDefaultValue = false)]
    public string? CorrelationId { get; set; }
    [DataMember(Name = "parameters", EmitDefaultValue = false)]
    public List<string>? Parameters { get; set; }
    [DataMember(Name = "sampleLimit", EmitDefaultValue = false)]
    public int? SampleLimit { get; set; }
    [DataMember(Name = "includeTypes", EmitDefaultValue = false)]
    public bool? IncludeTypes { get; set; }
    [DataMember(Name = "command")]
    public string? Command { get; set; }
    [DataMember(Name = "views")]
    public List<string>? Views { get; set; }
    [DataMember(Name = "view")]
    public string? View { get; set; }
    [DataMember(Name = "viewType")]
    public string? ViewType { get; set; }
    [DataMember(Name = "nameContains")]
    public string? NameContains { get; set; }
    [DataMember(Name = "categories")]
    public List<string>? Categories { get; set; }
    [DataMember(Name = "offset")]
    public int? Offset { get; set; }
    [DataMember(Name = "limit")]
    public int? Limit { get; set; }
    [DataMember(Name = "id")]
    public long? Id { get; set; }
    [DataMember(Name = "family")]
    public string? Family { get; set; }
    [DataMember(Name = "type")]
    public string? Type { get; set; }
    [DataMember(Name = "level")]
    public string? Level { get; set; }
    [DataMember(Name = "workset")]
    public string? Workset { get; set; }
    [DataMember(Name = "phase")]
    public string? Phase { get; set; }
    [DataMember(Name = "areaScheme")]
    public string? AreaScheme { get; set; }
    [DataMember(Name = "parameterFilters")]
    public List<ParameterFilterContract>? ParameterFilters { get; set; }
    [DataMember(Name = "fields")]
    public List<string>? Fields { get; set; }
    [DataMember(Name = "sort")]
    public QuerySortContract? Sort { get; set; }
    [DataMember(Name = "groupBy")]
    public List<string>? GroupBy { get; set; }
    [DataMember(Name = "numericField")]
    public string? NumericField { get; set; }
    [DataMember(Name = "section")]
    public string? Section { get; set; }
    [DataMember(Name = "warningText")]
    public string? WarningText { get; set; }
    [DataMember(Name = "includeGeometry")]
    public bool? IncludeGeometry { get; set; }
    [DataMember(Name = "includeElements")]
    public bool? IncludeElements { get; set; }
    [DataMember(Name = "relation")]
    public string? Relation { get; set; }
    [DataMember(Name = "sourceId")]
    public long? SourceId { get; set; }
    [DataMember(Name = "sourceName")]
    public string? SourceName { get; set; }
    [DataMember(Name = "pixelSize")]
    public int? PixelSize { get; set; }
    [DataMember(Name = "zoomToFit")]
    public bool? ZoomToFit { get; set; }
    [DataMember(Name = "targetDocument")]
    public string? TargetDocument { get; set; }
    [DataMember(Name = "targetProcessId")]
    public int? TargetProcessId { get; set; }
}

[DataContract]
public sealed class ParameterFilterContract
{
    [DataMember(Name = "parameter")]
    public string? Parameter { get; set; }
    [DataMember(Name = "operator")]
    public string? Operator { get; set; }
    [DataMember(Name = "value")]
    public string? Value { get; set; }
}

[DataContract]
public sealed class QuerySortContract
{
    [DataMember(Name = "field")]
    public string? Field { get; set; }
    [DataMember(Name = "direction")]
    public string? Direction { get; set; }
}
