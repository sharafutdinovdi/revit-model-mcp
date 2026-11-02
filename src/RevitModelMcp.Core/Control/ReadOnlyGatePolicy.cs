using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Control;

/// <summary>Decides which submitted commands the workstation read-only gate refuses on every transport.</summary>
public static class ReadOnlyGatePolicy
{
    public static bool RefusesSubmission(string? command) =>
        command is not null && command != "set-read-only" && (ActionJobParser.IsAction(command) || BatchReadOnlyPolicy.RefusedInReadOnlyMode(command));
}
