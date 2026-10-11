namespace RevitModelMcp.Core.Control;

/// <summary>Stored results for jobs that did not finish before Revit closed.</summary>
public static class ShutdownResults
{
    /// <summary>Reason reported for a job that never started.</summary>
    public const string ClosingReason = "Revit is closing";

    /// <summary>Result for a job that was queued or waiting when Revit closed; its state is cancelled.</summary>
    public const string Queued = "{\"success\":false,\"cancelled\":true,\"error\":\"" + ClosingReason + ".\"}";

    /// <summary>Result for a job that was running when Revit closed; its state is abandoned.</summary>
    public const string Running =
        "{\"success\":false,\"abandoned\":true,\"error\":\"Revit is closing while this job was running. The change may still have been applied; inspect the model before retrying.\"}";

    /// <summary>Result for a waiting caller whose job is unknown to the scheduler during shutdown.</summary>
    public const string Unknown = "{\"success\":false,\"error\":\"" + ClosingReason + ".\"}";
}
