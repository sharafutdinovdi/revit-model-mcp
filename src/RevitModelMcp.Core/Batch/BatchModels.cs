using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Batch;

public enum BatchModelStatus { Pending, Running, Completed, Failed, Cancelled }
public enum BatchRunStatus { Pending, Running, Completed, Cancelled }
public enum BatchPhase { Startup, PrePass, Open, Snapshot, Close }

[DataContract]
public sealed record BatchParameterRule
{
    [DataMember(Name = "category")] public required string Category { get; init; }
    [DataMember(Name = "parameter")] public required string Parameter { get; init; }
}

[DataContract]
public sealed record BatchModel
{
    [DataMember(Name = "path")] public required string Path { get; init; }
    [DataMember(Name = "status")] public BatchModelStatus Status { get; init; }
    [DataMember(Name = "phase")] public BatchPhase? Phase { get; init; }
    [DataMember(Name = "savedYear")] public int? SavedYear { get; init; }
    [DataMember(Name = "runtimeYear")] public int? RuntimeYear { get; init; }
    [DataMember(Name = "upgradedInMemory")] public bool UpgradedInMemory { get; init; }
    [DataMember(Name = "savedYearSource")] public string? SavedYearSource { get; init; }
    [DataMember(Name = "activitySource")] public string? ActivitySource { get; init; }
    [DataMember(Name = "error")] public string? Error { get; init; }
    [DataMember(Name = "snapshotFile")] public string? SnapshotFile { get; init; }
    [DataMember(Name = "phaseTimingsMs")] public Dictionary<string, long> PhaseTimingsMs { get; init; } = new();
    [DataMember(Name = "workerProcessId")] public int? WorkerProcessId { get; init; }
    [DataMember(Name = "workerStartedUtc")] public string? WorkerStartedUtc { get; init; }
    [DataMember(Name = "workerProcessStartedUtc")] public string? WorkerProcessStartedUtc { get; init; }
}

[DataContract]
public sealed record BatchRun
{
    [DataMember(Name = "runId")] public required string RunId { get; init; }
    [DataMember(Name = "status")] public BatchRunStatus Status { get; init; }
    [DataMember(Name = "cancelRequested")] public bool CancelRequested { get; init; }
    [DataMember(Name = "years")] public int[] Years { get; init; } = [];
    [DataMember(Name = "parameterRules")] public BatchParameterRule[] ParameterRules { get; init; } = [];
    [DataMember(Name = "models")] public required BatchModel[] Models { get; init; }
}

[DataContract]
public sealed record BatchLaunchRequest
{
    [DataMember(Name = "executables")]
    public required Dictionary<int, string> Executables { get; init; }
}
