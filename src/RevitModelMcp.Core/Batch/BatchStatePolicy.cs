namespace RevitModelMcp.Core.Batch;

public static class BatchStatePolicy
{
    public static bool IsTerminal(BatchModelStatus status) => status is BatchModelStatus.Completed or BatchModelStatus.Failed or BatchModelStatus.Cancelled;

    public static BatchRun Transition(BatchRun run, int index, BatchModelStatus next)
    {
        if (index < 0 || index >= run.Models.Length) throw new ArgumentOutOfRangeException(nameof(index));
        var model = run.Models[index];
        if (IsTerminal(model.Status)) throw new InvalidOperationException("A terminal model cannot transition.");
        if (run.CancelRequested && next is BatchModelStatus.Running or BatchModelStatus.Completed)
            throw new InvalidOperationException("A cancelled run cannot start or complete a model.");
        if (model.Status == BatchModelStatus.Pending && next is not (BatchModelStatus.Running or BatchModelStatus.Cancelled) ||
            model.Status == BatchModelStatus.Running && next is not (BatchModelStatus.Completed or BatchModelStatus.Failed or BatchModelStatus.Cancelled))
            throw new InvalidOperationException("Illegal model state transition.");
        var models = run.Models.ToArray();
        models[index] = model with { Status = next };
        return run with { Models = models, Status = ResolveStatus(models, run.CancelRequested) };
    }

    public static BatchRun Resume(BatchRun run)
    {
        if (run.Status == BatchRunStatus.Failed) return run;
        if (run.CancelRequested) return Cancel(run);
        var models = run.Models.Select(model => model.Status == BatchModelStatus.Running
            ? model with { Status = BatchModelStatus.Pending, Phase = null, WorkerProcessId = null, WorkerStartedUtc = null, WorkerProcessStartedUtc = null, SnapshotFile = null }
            : model).ToArray();
        return run with { Models = models, Status = ResolveStatus(models, false) };
    }

    public static BatchRun Cancel(BatchRun run)
    {
        var models = run.Models.Select(model => model.Status == BatchModelStatus.Pending
            ? model with { Status = BatchModelStatus.Cancelled } : model).ToArray();
        return run with { CancelRequested = true, Status = BatchRunStatus.Cancelled, Models = models };
    }

    public static BatchRun FailSupervisor(BatchRun run, string error)
    {
        var models = run.Models.Select(model => IsTerminal(model.Status)
            ? model : model with { Status = BatchModelStatus.Failed, Error = error }).ToArray();
        return run with { Status = BatchRunStatus.Failed, Models = models };
    }

    public static BatchModel Timed(BatchModel model, BatchPhase phase, long elapsed)
    {
        var timings = new Dictionary<string, long>(model.PhaseTimingsMs) { [phase.ToString()] = elapsed };
        return model with { Phase = phase, PhaseTimingsMs = timings };
    }

    public static BatchRunStatus ResolveStatus(IReadOnlyList<BatchModel> models, bool cancelled)
    {
        if (cancelled) return BatchRunStatus.Cancelled;
        if (models.All(model => IsTerminal(model.Status))) return BatchRunStatus.Completed;
        return models.Any(model => model.Status == BatchModelStatus.Running) ? BatchRunStatus.Running : BatchRunStatus.Pending;
    }
}
