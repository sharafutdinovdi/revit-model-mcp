using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Control;

/// <summary>Decides which submitted commands the workstation read-only gate refuses on every transport.</summary>
public static class ReadOnlyGatePolicy
{
    public const string Message = "read-only mode";

    public static bool RefusesSubmission(string? command) =>
        command is not null && (ActionJobParser.IsAction(command) || BatchReadOnlyPolicy.RefusedInReadOnlyMode(command));
}
