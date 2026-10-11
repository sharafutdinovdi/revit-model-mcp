namespace RevitModelMcp.Core.Control;

/// <summary>
/// Decides whether an action step is checked for an empty commit. A commit that changed no element is rolled
/// back so no undo entry is kept. Commands that act on the Revit UI or on temporary view state never change
/// elements, so an empty commit is their normal outcome and must not be reported as a failed change.
/// </summary>
public static class NoChangeGuardPolicy
{
    private static readonly HashSet<string> UiOnlyCommands = new(StringComparer.Ordinal)
    {
        "select", "show", "isolate", "override-graphics", "activate-view", "close-views", "set-view-visibility",
        "open-document", "activate-document", "close-document", "undo-last"
    };

    /// <summary>True when the command only drives the UI or temporary view state and is never checked for an empty commit.</summary>
    public static bool IsUiOnly(string command) => UiOnlyCommands.Contains(command);

    /// <summary>True when the step runs inside its own transaction group, is not a dry run and may change elements.</summary>
    public static bool Applies(string command, bool dryRun, bool hasGroup) => hasGroup && !dryRun && !IsUiOnly(command);

    /// <summary>
    /// True when a committed step may be recorded as the newest undoable action. UI-only commands never create a Revit
    /// undo entry, except graphic overrides and view visibility, which do only when the commit changed an element.
    /// Recording a phantom entry would make undo_last target it and strand the previous real action.
    /// </summary>
    public static bool KeepsUndoEntry(string command, int changedElementCount) =>
        !IsUiOnly(command) || (TracksUndoByChanges(command) && changedElementCount > 0);

    /// <summary>True when a UI-only command still counts commit changes to decide whether it left an undo entry.</summary>
    public static bool TracksUndoByChanges(string command) => command is "override-graphics" or "set-view-visibility";

    /// <summary>
    /// True when a finished job of this command that kept no undo entry must put undo tracking back to its state
    /// before the job. Only commands that commit no Revit transaction qualify (select, show, activate-view,
    /// close-views). Isolate, graphic overrides and view visibility run a real transaction whose commit can leave a
    /// Revit undo entry on top of the stack; restoring would hide it, and undo_last would then report success while
    /// Revit pops that entry instead of the targeted action.
    /// </summary>
    public static bool RestoresUndoStateWithoutEntry(string command) =>
        command is "select" or "show" or "activate-view" or "close-views";
}
