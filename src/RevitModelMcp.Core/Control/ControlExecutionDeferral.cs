using System.Diagnostics;

namespace RevitModelMcp.Core.Control;

public sealed class ControlExecutionDeferral(Func<long>? clock = null)
{
    private static readonly long MaximumDeferralTicks = 2 * Stopwatch.Frequency;
    private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;
    private string? _deferredJobId;
    private long _deferredSince;
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
        if (_deferredJobId != jobId || now < _deferredSince)
        {
            _deferredJobId = jobId;
            _deferredSince = now;
        }
        return now - _deferredSince >= MaximumDeferralTicks;
    }
}
