using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Activity;

public static class TabActivitySettings
{
    public static bool ShouldRestore<T>(T current, T applied) => EqualityComparer<T>.Default.Equals(current, applied);
}

public sealed record TabActivityState(string DocumentId, string ToolTip);

public sealed class TabActivityTracker(Func<DateTimeOffset> clock)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Indicator> _indicators = new(StringComparer.Ordinal);
    private bool _enabled = true;

    public void Configure(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _jobs.Clear();
            _indicators.Clear();
        }
    }

    public void Start(string jobId, string documentId, string clientName, string command, string? actionSummary = null, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job id is required.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(documentId)) throw new ArgumentException("Document id is required.", nameof(documentId));
        if (!_enabled) return;
        Advance();
        var now = _clock();
        var job = new Job(documentId, clientName, actionSummary ?? command, ActionJobParser.IsAction(command));
        _jobs.Add(jobId, job);
        if (job.IsAction || command is "export-view" or "model-snapshot" or "batch-snapshot" or "model-health" ||
            command == "family-audit" && detail == "full") Show(job, now);
        if (_indicators.TryGetValue(documentId, out var indicator))
        {
            indicator.Ended = null;
            indicator.ToolTip = ToolTip(job);
        }
    }

    public void MarkVisible(string documentId)
    {
        if (!_indicators.TryGetValue(documentId, out var indicator) || indicator.Visible) return;
        indicator.Shown = _clock();
        indicator.Visible = true;
    }

    public void Progress(string jobId, int? step, int? total, string? actionSummary = null)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.Step = step;
        job.Total = total;
        if (job.IsAction && actionSummary is not null && !string.IsNullOrWhiteSpace(actionSummary)) job.Summary = actionSummary;
        if (_indicators.TryGetValue(job.DocumentId, out var indicator)) indicator.ToolTip = ToolTip(job);
    }

    public void End(string jobId, DateTimeOffset? responseDeliveredAt = null)
    {
        Advance();
        if (!_jobs.Remove(jobId, out var job)) return;
        if (!_indicators.TryGetValue(job.DocumentId, out var indicator)) return;
        var remaining = _jobs.Values.LastOrDefault(candidate => candidate.DocumentId == job.DocumentId);
        if (remaining is null) indicator.Ended = responseDeliveredAt ?? _clock();
        else indicator.ToolTip = ToolTip(remaining);
    }

    public IReadOnlyList<TabActivityState> Snapshot()
    {
        Advance();
        return _indicators.Select(pair => new TabActivityState(pair.Key, pair.Value.ToolTip)).ToArray();
    }

    public TimeSpan? NextChange
    {
        get
        {
            var now = _clock();
            var deadlines = _indicators.Values.Where(indicator => indicator.Ended.HasValue)
                .Select(indicator => Later(indicator.Shown.AddSeconds(1.5), indicator.Ended!.Value.AddSeconds(1)))
                .ToArray();
            if (deadlines.Length == 0) return null;
            var delay = deadlines.Min() - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    private void Advance()
    {
        var now = _clock();
        foreach (var pair in _indicators.ToArray())
            if (pair.Value.Ended is { } ended && now >= Later(pair.Value.Shown.AddSeconds(1.5), ended.AddSeconds(1)))
                _indicators.Remove(pair.Key);
    }

    private void Show(Job job, DateTimeOffset now)
    {
        if (!_indicators.ContainsKey(job.DocumentId)) _indicators.Add(job.DocumentId, new Indicator(now, ToolTip(job)));
    }

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    private static string ToolTip(Job job) => $"Model MCP: {job.ClientName} running {job.Summary}" +
        (job.Step is >= 0 && job.Total is > 0 ? $", step {job.Step} of {job.Total}" : string.Empty);

    private sealed class Job(string documentId, string clientName, string summary, bool isAction)
    {
        public string DocumentId { get; } = documentId;
        public string ClientName { get; } = clientName;
        public string Summary { get; set; } = summary;
        public bool IsAction { get; } = isAction;
        public int? Step { get; set; }
        public int? Total { get; set; }
    }

    private sealed class Indicator(DateTimeOffset shown, string toolTip)
    {
        public DateTimeOffset Shown { get; set; } = shown;
        public bool Visible { get; set; }
        public DateTimeOffset? Ended { get; set; }
        public string ToolTip { get; set; } = toolTip;
    }
}
