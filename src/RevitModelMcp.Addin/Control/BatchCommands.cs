using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Batch;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal static class BatchCommands
{
    internal static void Execute(UIApplication application, ControlJobParseResult job, DateTimeOffset startedAt)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            switch (job.Kind)
            {
                case ControlJobKind.BatchSupervisorStart:
                    Write(application, job, startedAt, stopwatch, StartSupervisor(job.CoordinatorJob.RunId));
                    break;
                case ControlJobKind.BatchPrePass:
                    Write(application, job, startedAt, stopwatch, PrePass(job.CoordinatorJob.Path));
                    break;
                case ControlJobKind.BatchOpen:
                    ExecuteModelPhase(application, job, startedAt, stopwatch, "open",
                        () => DocumentActions.BatchOpen(application, job.CoordinatorJob.Path!));
                    break;
                case ControlJobKind.BatchClose:
                    ExecuteModelPhase(application, job, startedAt, stopwatch, "close", DocumentActions.BatchClose);
                    break;
                case ControlJobKind.BatchSnapshot:
                    ExecuteModelPhase(application, job, startedAt, stopwatch, "snapshot",
                        () => ModelSnapshotReader.Read(DocumentActions.BatchDocument, job.ParameterRules));
                    break;
                default: throw new ArgumentException("Unknown batch command.");
            }
        }
        catch (Exception exception)
        {
            var response = CommandResponse<object>.Fail(job.Command, exception.Message, stopwatch.ElapsedMilliseconds);
            CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
                ReadCommandReader.ReadResponder(application), job.CorrelationId).Write(response);
        }
    }

    private static void Write<T>(UIApplication application, ControlJobParseResult job,
        DateTimeOffset startedAt, Stopwatch stopwatch, T data)
    {
        CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application), job.CorrelationId)
            .Write(CommandResponse<T>.Ok(job.Command, data, stopwatch.ElapsedMilliseconds));
    }

    private static BatchStartResult StartSupervisor(string? runId)
    {
        if (Environment.GetEnvironmentVariable("REVIT_MCP_BATCH_WORKER") == "1")
            throw new InvalidOperationException("A batch worker cannot start a supervisor.");
        if (!Guid.TryParseExact(runId, "N", out _)) throw new ArgumentException("Invalid run id.");
        var root = Path.Combine(SnapshotFileWriter.RootDirectory, "runs", runId!);
        if (!File.Exists(Path.Combine(root, "run.json"))) throw new FileNotFoundException("Batch run state is absent.");
        var executables = new Dictionary<int, string>();
        for (var year = 2022; year <= 2027; year++)
        {
            var configured = Environment.GetEnvironmentVariable($"REVIT_MCP_REVIT_EXE_{year}");
            var path = configured ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Autodesk", $"Revit {year}", "Revit.exe");
            if (File.Exists(path)) executables.Add(year, path);
        }
        if (executables.Count == 0) throw new InvalidOperationException("No configured Revit executable is installed.");
        var launch = new BatchLaunchRequest { Executables = executables };
        var serializer = new DataContractJsonSerializer(typeof(BatchLaunchRequest),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        var temporary = Path.Combine(root, $"launch.{Guid.NewGuid():N}.tmp");
        using (var stream = File.Create(temporary)) serializer.WriteObject(stream, launch);
        var destination = Path.Combine(root, "launch.json");
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
        var executable = Path.Combine(Path.GetDirectoryName(typeof(BatchCommands).Assembly.Location)!,
            "RevitModelMcp.BatchSupervisor.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Installed batch supervisor is absent.");
        Process.Start(new ProcessStartInfo(executable, $"\"{root}\"") { UseShellExecute = false });
        return new BatchStartResult { RunId = runId! };
    }

    private static BatchPrePassResult PrePass(string? path)
    {
        EnsureWorker();
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Local or UNC model path is required for BasicFileInfo.");
        using var info = BasicFileInfo.Extract(path);
        return new BatchPrePassResult
        {
            SavedYear = BatchYearRouter.Parse(info.Format),
            Format = info.Format,
            Source = "BasicFileInfo.Format"
        };
    }

    private static void ExecuteModelPhase<T>(UIApplication application, ControlJobParseResult job,
        DateTimeOffset startedAt, Stopwatch stopwatch, string phase, Func<T> operation)
    {
        BatchDialogHandler? dialogHandler = null;
        var published = false;
        var publishSync = new object();
        var writer = CommandResponseFileWriter.Create(startedAt.LocalDateTime, job.Command,
            ReadCommandReader.ReadResponder(application), job.CorrelationId);
        void Publish(T? result, string? error)
        {
            lock (publishSync)
            {
                if (published) return;
                published = true;
                var data = new BatchPhaseResult<T> { Dialogs = dialogHandler?.Dialogs ?? [], Result = result };
                var response = error is null
                    ? CommandResponse<BatchPhaseResult<T>>.Ok(job.Command, data, stopwatch.ElapsedMilliseconds)
                    : CommandResponse<BatchPhaseResult<T>>.Fail(job.Command, error, stopwatch.ElapsedMilliseconds);
                response.Data = data;
                writer.Write(response);
            }
        }
        try
        {
            EnsureWorker();
            var path = job.CoordinatorJob.Path;
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Model path is required.");
            var allowlistPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitModelMcp", "batch-dialogs.json");
            var choices = BatchDialogPolicy.Load(allowlistPath);
            dialogHandler = new BatchDialogHandler(path, phase, choices, unknown =>
                Publish(default, BatchDialogText.DescribeUnknown(unknown)));
            application.DialogBoxShowing += dialogHandler.OnDialog;
            var result = operation();
            dialogHandler.WaitForPendingCapture();
            if (dialogHandler.UnknownDialog is not null)
                throw new InvalidOperationException("Unknown modal dialog interrupted the batch phase.");
            Publish(result, null);
        }
        catch (Exception exception)
        {
            dialogHandler?.WaitForPendingCapture();
            var unknown = dialogHandler?.UnknownDialog;
            var reason = unknown is null ? exception.Message :
                BatchDialogText.DescribeUnknown(unknown);
            Publish(default, reason);
        }
        finally
        {
            if (dialogHandler is not null) application.DialogBoxShowing -= dialogHandler.OnDialog;
        }
    }

    private static void EnsureWorker()
    {
        if (Environment.GetEnvironmentVariable("REVIT_MCP_BATCH_WORKER") != "1")
            throw new InvalidOperationException("Batch model commands require a supervised worker.");
    }

    [DataContract]
    private sealed record BatchPrePassResult
    {
        [DataMember(Name = "savedYear")] public int SavedYear { get; init; }
        [DataMember(Name = "format")] public required string Format { get; init; }
        [DataMember(Name = "source")] public required string Source { get; init; }
    }
}
