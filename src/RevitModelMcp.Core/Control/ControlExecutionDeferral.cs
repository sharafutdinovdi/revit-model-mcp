namespace RevitModelMcp.Core.Control;

public sealed class ControlExecutionDeferral(Func<DateTimeOffset>? clock = null)
{
    private static readonly TimeSpan MaximumDeferral = TimeSpan.FromSeconds(2);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private string? _deferredJobId;
    private DateTimeOffset _deferredSince;
    private Func<string, string, bool>? _canExecute;

    public ScheduledJob? TakeNext(JobScheduler scheduler, bool fromIdling)
    {
        if (!fromIdling) Reset();
        var job = scheduler.TakeNext(fromIdling ? _canExecute ??= CanExecute : null);
        if (job is not null || !scheduler.HasPending) Reset();
        return job;
    }

    public void Reset() => _deferredJobId = null;

    private bool CanExecute(string jobId, string command)
    {
        if (ControlExecutionPolicy.CanExecuteFromIdling(command)) return true;
        var now = _clock();
        if (_deferredJobId != jobId)
        {
            _deferredJobId = jobId;
            _deferredSince = now;
        }
        return now - _deferredSince >= MaximumDeferral;
    }
}
