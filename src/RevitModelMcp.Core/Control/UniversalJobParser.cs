using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Control;

internal static class UniversalJobParser
{
    private static readonly HashSet<string> CatalogSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "categories", "family-types", "levels", "area-schemes", "views", "worksets", "phases", "parameters"
    };

    private static readonly HashSet<string> Relations = new(StringComparer.OrdinalIgnoreCase)
    {
        "level-rooms", "group-elements", "nested-family", "area-scheme-elements", "view-template-dependents"
    };

    public static ControlJobParseResult ParseQuery(ControlJobContract job)
    {
        var common = ParseCommon(job, "query-elements");
        if (common.Error is not null)
        {
            return common.Error;
        }

        var result = ControlJobParseResult.Create(ControlJobKind.QueryElements, "query-elements");
        ApplyCommon(result, common);
        result.Fields = NormalizeMany(job.Fields);
        result.IncludeGeometry = job.IncludeGeometry ?? false;
        result.Sort = ParseSort(job.Sort, out var sortError);
        return sortError is null ? result : ControlJobParseResult.Invalid("query-elements", sortError);
    }

    public static ControlJobParseResult ParseAggregate(ControlJobContract job)
    {
        var common = ParseCommon(job, "aggregate-elements");
        if (common.Error is not null)
        {
            return common.Error;
        }

        var groupBy = NormalizeMany(job.GroupBy);
        if (groupBy.Count is < 1 or > 2)
        {
            return ControlJobParseResult.Invalid("aggregate-elements", "The aggregate-elements command requires one or two groupBy fields.");
        }

        var result = ControlJobParseResult.Create(ControlJobKind.AggregateElements, "aggregate-elements");
        ApplyCommon(result, common);
        result.GroupBy = groupBy;
        result.NumericField = Normalize(job.NumericField);
        return result;
    }

    public static ControlJobParseResult ParseCatalog(ControlJobContract job)
    {
        var section = Normalize(job.Section);
        return section is null || !CatalogSections.Contains(section)
            ? ControlJobParseResult.Invalid(
                "list-catalog",
                $"Unknown catalog section: {section ?? "<empty>"}. Available sections: {string.Join(", ", CatalogSections)}.")
            : WithCatalog(section);
    }

    public static ControlJobParseResult ParseWarnings(ControlJobContract job)
    {
        var result = ControlJobParseResult.Create(ControlJobKind.ListWarnings, "list-warnings");
        result.WarningText = Normalize(job.WarningText);
        result.IncludeElements = job.IncludeElements ?? false;
        return result;
    }

    public static ControlJobParseResult ParseRelations(ControlJobContract job)
    {
        var relation = Normalize(job.Relation);
        if (relation is null || !Relations.Contains(relation))
        {
            return ControlJobParseResult.Invalid(
                "list-relations",
                $"Unknown relation: {relation ?? "<empty>"}. Available relations: {string.Join(", ", Relations)}.");
        }

        var sourceName = Normalize(job.SourceName);
        var needsId = relation is "group-elements" or "nested-family";
        if (needsId && (!job.SourceId.HasValue || job.SourceId <= 0))
        {
            return ControlJobParseResult.Invalid("list-relations", $"Relation {relation} requires a positive sourceId.");
        }

        if (!needsId && sourceName is null)
        {
            return ControlJobParseResult.Invalid("list-relations", $"Relation {relation} requires sourceName.");
        }

        var result = ControlJobParseResult.Create(ControlJobKind.ListRelations, "list-relations");
        result.Relation = relation;
        result.SourceId = job.SourceId;
        result.SourceName = sourceName;
        return result;
    }

    private static CommonParseResult ParseCommon(ControlJobContract job, string command)
    {
        var offset = job.Offset ?? 0;
        var limit = job.Limit ?? 100;
        if (offset < 0 || limit <= 0)
        {
            return CommonParseResult.Fail(ControlJobParseResult.Invalid(
                command,
                offset < 0 ? "The offset must not be negative." : "The limit must be greater than zero."));
        }

        var parameters = new List<ParameterFilterSpec>();
        foreach (var contract in job.ParameterFilters ?? new List<ParameterFilterContract>())
        {
            var parsed = ParseParameter(contract, command);
            if (parsed.Error is not null)
            {
                return CommonParseResult.Fail(parsed.Error);
            }

            parameters.Add(parsed.Filter!);
        }

        return new CommonParseResult
        {
            Offset = offset,
            Limit = limit,
            Filters = new ElementFilterSpec
            {
                Categories = NormalizeMany(job.Categories),
                Family = Normalize(job.Family),
                Type = Normalize(job.Type),
                Level = Normalize(job.Level),
                View = Normalize(job.View),
                Workset = Normalize(job.Workset),
                Phase = Normalize(job.Phase),
                AreaScheme = Normalize(job.AreaScheme),
                Parameters = parameters
            }
        };
    }

    private static ParameterParseResult ParseParameter(ParameterFilterContract contract, string command)
    {
        var name = Normalize(contract.Parameter);
        var operation = Normalize(contract.Operator)?.ToLowerInvariant();
        if (name is null || !TryParseOperator(operation, out var parsed))
        {
            return ParameterParseResult.Fail(ControlJobParseResult.Invalid(
                command,
                $"Invalid parameter filter: parameter={name ?? "<empty>"}, operator={operation ?? "<empty>"}."));
        }

        var value = Normalize(contract.Value);
        if (parsed is ParameterOperator.Equals or ParameterOperator.Contains or ParameterOperator.Greater or ParameterOperator.Less && value is null)
        {
            return ParameterParseResult.Fail(ControlJobParseResult.Invalid(command, $"Operator {operation} for parameter {name} requires value."));
        }

        return new ParameterParseResult { Filter = new ParameterFilterSpec { Parameter = name, Operator = parsed, Value = value } };
    }

    private static bool TryParseOperator(string? value, out ParameterOperator result)
    {
        result = value switch
        {
            "equals" or "equal" or "eq" => ParameterOperator.Equals,
            "contains" => ParameterOperator.Contains,
            "greater" or "gt" => ParameterOperator.Greater,
            "less" or "lt" => ParameterOperator.Less,
            "empty" => ParameterOperator.Empty,
            "not-empty" or "filled" => ParameterOperator.NotEmpty,
            "exists" => ParameterOperator.Exists,
            _ => default
        };
        return value is "equals" or "equal" or "eq" or "contains" or "greater" or "gt" or "less" or "lt" or
            "empty" or "not-empty" or "filled" or "exists";
    }

    private static QuerySortSpec ParseSort(QuerySortContract? sort, out string? error)
    {
        var direction = Normalize(sort?.Direction)?.ToLowerInvariant() ?? "asc";
        error = direction is "asc" or "desc" ? null : "The sort.direction must be asc or desc.";
        return new QuerySortSpec { Field = Normalize(sort?.Field) ?? "id", Descending = direction == "desc" };
    }

    private static void ApplyCommon(ControlJobParseResult result, CommonParseResult common)
    {
        result.Filters = common.Filters;
        result.Categories = common.Filters.Categories;
        result.View = common.Filters.View;
        result.Offset = common.Offset;
        result.Limit = common.Limit;
    }

    private static ControlJobParseResult WithCatalog(string section)
    {
        var result = ControlJobParseResult.Create(ControlJobKind.ListCatalog, "list-catalog");
        result.CatalogSection = section;
        return result;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    private static IReadOnlyList<string> NormalizeMany(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>()).Select(Normalize).Where(value => value is not null).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private sealed class CommonParseResult
    {
        public ElementFilterSpec Filters { get; set; } = new();
        public int Offset { get; set; }
        public int Limit { get; set; }
        public ControlJobParseResult? Error { get; set; }
        public static CommonParseResult Fail(ControlJobParseResult error) => new() { Error = error };
    }

    private sealed class ParameterParseResult
    {
        public ParameterFilterSpec? Filter { get; set; }
        public ControlJobParseResult? Error { get; set; }
        public static ParameterParseResult Fail(ControlJobParseResult error) => new() { Error = error };
    }
}

public sealed class ExternalEventRequestQueue
{
    private readonly object _sync = new();
    private readonly Func<bool> _raise;
    private readonly Action? _markWaiting;
    private bool _executing;
    private bool _requested; // A single flag preserves requests received during Execute.
    private bool _retryScheduled;
    public ExternalEventRequestQueue(Action raise)
        : this(() => { raise(); return true; }, null)
    {
    }
    public ExternalEventRequestQueue(Func<bool> raise, Action? markWaiting = null)
    {
        _raise = raise ?? throw new ArgumentNullException(nameof(raise));
        _markWaiting = markWaiting;
    }
    public void Request()
    {
        var shouldRaise = false;
        lock (_sync)
        {
            if (_requested)
            {
                return;
            }

            _requested = true;
            shouldRaise = !_executing;
        }
        if (shouldRaise)
        {
            RaisePendingRequest();
            MarkWaitingIfDelayed();
        }
    }

    public void Execute(Action handler, Action<Exception> writeError)
    {
        lock (_sync)
        {
            if (_executing)
            {
                return;
            }

            _executing = true;
            _requested = false;
        }
        try
        {
            handler();
        }
        catch (Exception exception)
        {
            writeError(exception);
        }
        finally
        {
            var shouldRaise = false;
            lock (_sync)
            {
                _executing = false;
                shouldRaise = _requested;
            }
            if (shouldRaise)
            {
                RaisePendingRequest();
                MarkWaitingIfDelayed();
            }
        }
    }

    private void RaisePendingRequest()
    {
        try
        {
            if (!_raise())
            {
                lock (_sync)
                {
                    if (_retryScheduled) return;
                    _retryScheduled = true;
                }
                _ = Task.Delay(100).ContinueWith(_ =>
                {
                    lock (_sync) _retryScheduled = false;
                    lock (_sync)
                    {
                        if (!_requested || _executing) return;
                    }
                    RaisePendingRequest();
                }, TaskScheduler.Default);
            }
        }
        catch
        {
            lock (_sync)
            {
                _requested = false;
            }
            throw;
        }
    }

    private void MarkWaitingIfDelayed()
    {
        if (_markWaiting is null) return;
        _ = Task.Delay(500).ContinueWith(_ =>
        {
            lock (_sync)
            {
                if (!_requested || _executing) return;
            }
            _markWaiting();
        }, TaskScheduler.Default);
    }
}

public sealed class TriggerFileWatcher : IDisposable
{
    private static readonly TimeSpan MinimumFallbackInterval = TimeSpan.FromSeconds(10);
    private readonly string _triggerFilePath;
    private readonly Action _request;
    private readonly Action<Exception> _reportError;
    private readonly TimeSpan _fallbackInterval;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _timer;
    public TriggerFileWatcher(
        string triggerFilePath,
        Action request,
        Action<Exception> reportError,
        TimeSpan fallbackInterval)
    {
        if (fallbackInterval < MinimumFallbackInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fallbackInterval),
                "The fallback check for trigger.txt must not run more than once every 10 seconds.");
        }
        _triggerFilePath = Path.GetFullPath(triggerFilePath);
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _fallbackInterval = fallbackInterval;
    }
    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }
        var directory = Path.GetDirectoryName(_triggerFilePath)
                        ?? throw new InvalidOperationException("The trigger.txt file has no parent directory.");
        Directory.CreateDirectory(directory);
        _watcher = new FileSystemWatcher(directory, "*")
        {
            NotifyFilter = NotifyFilters.FileName
        };
        _watcher.Created += OnTriggerAppeared;
        _watcher.Renamed += OnTriggerRenamed;
        _watcher.EnableRaisingEvents = true;
        _timer = new System.Threading.Timer(
            _ => RequestIfTriggerExists(),
            null,
            _fallbackInterval,
            _fallbackInterval);
        RequestIfTriggerExists();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnTriggerAppeared;
            _watcher.Renamed -= OnTriggerRenamed;
            _watcher.Dispose();
            _watcher = null;
        }
    }
    private void OnTriggerAppeared(object sender, FileSystemEventArgs args)
    {
        if (IsJobFile(args.Name)) RequestSafely();
    }
    private void OnTriggerRenamed(object sender, RenamedEventArgs args)
    {
        if (IsJobFile(args.Name)) RequestSafely();
    }
    private bool IsJobFile(string? name) => name == Path.GetFileName(_triggerFilePath) ||
        name is not null && name.StartsWith("job_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);
    private void RequestIfTriggerExists()
    {
        try
        {
            if (File.Exists(_triggerFilePath) || Directory.GetFiles(Path.GetDirectoryName(_triggerFilePath)!, "job_*.json").Length > 0)
            {
                _request();
            }
        }
        catch (Exception exception)
        {
            _reportError(exception);
        }
    }

    private void RequestSafely()
    {
        try
        {
            _request();
        }
        catch (Exception exception)
        {
            _reportError(exception);
        }
    }
}
