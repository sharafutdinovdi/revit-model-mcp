using System.Diagnostics;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.BatchSupervisor;

internal sealed class BatchSupervisor(
    BatchRunStore store,
    FileChannelWorkerClient channel,
    RevitServerClient revitServer,
    Func<DateTimeOffset> clock)
{
    private readonly BatchRunStore _store = store;
    private readonly FileChannelWorkerClient _channel = channel;
    private readonly RevitServerClient _revitServer = revitServer;
    private readonly Func<DateTimeOffset> _clock = clock;
    private readonly string _channelRoot = Path.GetFullPath(Path.Combine(store.DirectoryPath, "..", ".."));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var launch = _store.ReadLaunch();
        var persisted = _store.Read();
        foreach (var interrupted in persisted.Models.Where(model => model.Status == BatchModelStatus.Running &&
                     model.WorkerProcessId is not null && model.WorkerProcessStartedUtc is not null &&
                     model.WorkerStartedUtc is not null))
            RevitWorkerProcess.RecycleInterrupted(interrupted.WorkerProcessId!.Value,
                interrupted.WorkerProcessStartedUtc!, interrupted.WorkerStartedUtc!, _channelRoot);
        var run = BatchStatePolicy.Resume(persisted);
        if (_store.CancellationRequested) run = BatchStatePolicy.Cancel(run);
        _store.Write(run);
        for (var index = 0; index < run.Models.Length; index++)
        {
            if (run.CancelRequested || _store.CancellationRequested)
            {
                _store.Write(BatchStatePolicy.Cancel(run));
                return;
            }
            if (run.Models[index].Status != BatchModelStatus.Pending) continue;
            run = BatchStatePolicy.Transition(run, index, BatchModelStatus.Running);
            _store.Write(run);
            var model = run.Models[index];
            try
            {
                model = await CollectAsync(run, model, index, launch.Executables, cancellationToken);
                run = Replace(run, index, model);
                run = BatchStatePolicy.Transition(run, index, BatchModelStatus.Completed);
            }
            catch (OperationCanceledException) when (_store.CancellationRequested)
            {
                model = _store.Read().Models[index];
                run = Replace(run, index, model with { Error = "Batch run cancelled." });
                run = BatchStatePolicy.Transition(run, index, BatchModelStatus.Cancelled);
                run = BatchStatePolicy.Cancel(run);
            }
            catch (Exception exception)
            {
                model = _store.Read().Models[index];
                run = Replace(run, index, model with { Error = exception.Message });
                run = BatchStatePolicy.Transition(run, index, BatchModelStatus.Failed);
            }
            _store.Write(run);
        }
    }

    private async Task<BatchModel> CollectAsync(BatchRun run, BatchModel model, int index,
        Dictionary<int, string> executables, CancellationToken cancellationToken)
    {
        var allowed = executables.Keys.Where(year => run.Years.Length == 0 || run.Years.Contains(year)).OrderBy(year => year).ToArray();
        if (allowed.Length == 0) throw new InvalidOperationException("No allowed installed Revit executable is available.");
        RevitWorkerProcess? worker = null;
        var opened = false;
        Exception? closeFailure = null;
        try
        {
            var savedYear = 0;
            var prepassStopwatch = Stopwatch.StartNew();
            var savedSource = "";
            var activitySource = (string?)null;
            if (model.Path.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
            {
                var rsn = await _revitServer.PrePassAsync(model.Path, cancellationToken);
                savedYear = rsn.SavedYear;
                savedSource = "RSN /contents ProductVersion";
                activitySource = rsn.ActivitySource;
            }
            else
            {
                model = model with { Phase = BatchPhase.Startup };
                _store.Write(Replace(run, index, model));
                var startupStopwatch = Stopwatch.StartNew();
                worker = await StartWorkerAsync(executables[allowed[allowed.Length - 1]], cancellationToken);
                model = Timed(model, BatchPhase.Startup, startupStopwatch.ElapsedMilliseconds) with
                { WorkerProcessId = worker.ProcessId, WorkerStartedUtc = worker.HeartbeatStartedUtc, WorkerProcessStartedUtc = worker.ProcessStartedUtc };
                _store.Write(Replace(run, index, model));
                model = model with { Phase = BatchPhase.PrePass };
                _store.Write(Replace(run, index, model));
                prepassStopwatch.Restart();
                var prepass = await PhaseAsync(worker, "batch-prepass", new() { ["path"] = model.Path },
                    BatchPhase.PrePass, TimeSpan.FromMinutes(2), cancellationToken);
                var data = Data(prepass);
                savedYear = Convert.ToInt32(data["savedYear"]);
                savedSource = Convert.ToString(data["source"]) ?? "BasicFileInfo.Format";
            }
            model = Timed(model, BatchPhase.PrePass, prepassStopwatch.ElapsedMilliseconds);
            var route = BatchYearRouter.Route(savedYear, executables.Keys, run.Years.Length == 0 ? null : run.Years);
            model = model with
            {
                SavedYear = savedYear,
                SavedYearSource = savedSource,
                ActivitySource = activitySource,
                RuntimeYear = route.RuntimeYear,
                UpgradedInMemory = route.UpgradedInMemory
            };
            _store.Write(Replace(run, index, model));
            if (route.RuntimeYear is null) throw new InvalidOperationException(route.Error);
            if (worker is not null && route.RuntimeYear != allowed[allowed.Length - 1])
            {
                worker.Recycle();
                worker.Dispose();
                worker = null;
            }
            if (worker is null)
            {
                model = model with { Phase = BatchPhase.Startup, WorkerProcessId = null, WorkerStartedUtc = null, WorkerProcessStartedUtc = null };
                _store.Write(Replace(run, index, model));
                var startupStopwatch = Stopwatch.StartNew();
                worker = await StartWorkerAsync(executables[route.RuntimeYear.Value], cancellationToken);
                model = Timed(model, BatchPhase.Startup, startupStopwatch.ElapsedMilliseconds);
            }
            model = model with
            {
                WorkerProcessId = worker.ProcessId,
                WorkerStartedUtc = worker.HeartbeatStartedUtc,
                WorkerProcessStartedUtc = worker.ProcessStartedUtc,
                Phase = BatchPhase.Open
            };
            _store.Write(Replace(run, index, model));
            var stopwatch = Stopwatch.StartNew();
            await PhaseAsync(worker, "batch-open", new() { ["path"] = model.Path },
                BatchPhase.Open, TimeSpan.FromMinutes(10), cancellationToken);
            opened = true;
            model = Timed(model, BatchPhase.Open, stopwatch.ElapsedMilliseconds);
            _store.Write(Replace(run, index, model));
            stopwatch.Restart();
            model = model with { Phase = BatchPhase.Snapshot };
            _store.Write(Replace(run, index, model));
            var snapshot = await PhaseAsync(worker, "model-snapshot", new()
            {
                ["parameterRules"] = run.ParameterRules.Select(rule => new Dictionary<string, string>
                {
                    ["category"] = rule.Category,
                    ["parameter"] = rule.Parameter
                }).ToArray()
            }, BatchPhase.Snapshot, TimeSpan.FromMinutes(20), cancellationToken);
            model = Timed(model, BatchPhase.Snapshot, stopwatch.ElapsedMilliseconds);
            var snapshotName = $"snapshot_{index + 1:D4}.json";
            _store.WriteSnapshot(snapshotName, _channel.SerializeData(snapshot));
            model = model with { SnapshotFile = snapshotName };
            _store.Write(Replace(run, index, model));
        }
        finally
        {
            if (worker is not null)
            {
                if (opened && !worker.HasExited)
                {
                    try
                    {
                        model = model with { Phase = BatchPhase.Close };
                        _store.Write(Replace(run, index, model));
                        var stopwatch = Stopwatch.StartNew();
                        await PhaseAsync(worker, "batch-close", new(), BatchPhase.Close,
                            TimeSpan.FromMinutes(2), CancellationToken.None, ignoreCancellation: true);
                        model = Timed(model, BatchPhase.Close, stopwatch.ElapsedMilliseconds);
                        _store.Write(Replace(run, index, model));
                    }
                    catch (Exception exception) { closeFailure = exception; }
                }
                try { worker.Recycle(); }
                catch (InvalidOperationException) { }
                worker.Dispose();
            }
        }
        if (closeFailure is not null) throw new InvalidOperationException("Batch document could not close without saving.", closeFailure);
        return model;
    }

    private async Task<RevitWorkerProcess> StartWorkerAsync(string executable, CancellationToken cancellationToken)
    {
        var worker = RevitWorkerProcess.Start(executable, _channelRoot);
        try
        {
            var started = await _channel.WaitForHeartbeatAsync(worker.ProcessId, _channelRoot,
                _clock().AddMinutes(3), () => _store.CancellationRequested, () => worker.HasExited,
                cancellationToken);
            worker.SetHeartbeatIdentity(started);
            return worker;
        }
        catch
        {
            try { worker.Recycle(); }
            catch (InvalidOperationException) { }
            worker.Dispose();
            throw;
        }
    }

    private Task<Dictionary<string, object>> PhaseAsync(RevitWorkerProcess worker, string command,
        Dictionary<string, object> payload, BatchPhase phase, TimeSpan budget,
        CancellationToken cancellationToken, bool ignoreCancellation = false) =>
        _channel.SendAsync(worker.ProcessId, worker.HeartbeatStartedUtc!, _channelRoot,
            command, payload, _clock().Add(budget), () => !ignoreCancellation && _store.CancellationRequested,
            () => worker.HasExited, cancellationToken);

    private static Dictionary<string, object> Data(Dictionary<string, object> response) =>
        response.TryGetValue("data", out var value) && value is Dictionary<string, object> data
            ? data : throw new InvalidDataException("Worker response has no data object.");

    private static BatchModel Timed(BatchModel model, BatchPhase phase, long elapsed)
    {
        var timings = new Dictionary<string, long>(model.PhaseTimingsMs) { [phase.ToString()] = elapsed };
        return model with { Phase = phase, PhaseTimingsMs = timings };
    }

    private static BatchRun Replace(BatchRun run, int index, BatchModel model)
    {
        var models = (BatchModel[])run.Models.Clone();
        models[index] = model;
        return run with { Models = models };
    }
}
