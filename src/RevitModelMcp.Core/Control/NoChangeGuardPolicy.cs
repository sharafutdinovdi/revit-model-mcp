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
    /// before the job. The command's own commit still fires <c>DocumentChanged</c> under its transaction name
    /// (<c>revit_isolate</c>), which would otherwise hide the previous real action from <c>revit_undo_last</c>.
    /// Excludes commands whose undo-stack effect arrives later (undo-last) or concerns another document.
    /// </summary>
    public static bool RestoresUndoStateWithoutEntry(string command) =>
        IsUiOnly(command) && command is not ("undo-last" or "open-document" or "activate-document" or "close-document");
}
