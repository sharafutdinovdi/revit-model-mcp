using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Control;

/// <summary>Decides which submitted commands the workstation read-only gate refuses on every transport.</summary>
public static class ReadOnlyGatePolicy
{
    public static bool RefusesSubmission(string? command) =>
        command is not null && command is not ("set-read-only" or "list-tools" or "get-tool") && (ActionJobParser.IsAction(command) || BatchReadOnlyPolicy.RefusedInReadOnlyMode(command));
}
