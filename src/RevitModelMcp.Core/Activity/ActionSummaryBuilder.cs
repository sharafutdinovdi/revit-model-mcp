namespace RevitModelMcp.Core.Activity;

public enum CodeFailureKind { None, Compilation, Execution }

/// <summary>Pure input for <see cref="ActionSummaryBuilder"/>; carries only primitive facts about one action result.</summary>
public sealed class ActionSummaryContext
{
    public string Command { get; init; } = string.Empty;
    public string DocumentTitle { get; init; } = string.Empty;
    public int Count { get; init; }
    public bool DryRun { get; init; }
    public string? Family { get; init; }
    public string? TypeName { get; init; }
    public string? Parameter { get; init; }
    public string? WallType { get; init; }
    public bool Reset { get; init; }
    public bool CadLink { get; init; }
    public int BatchStepCount { get; init; }
    public int ProcessTotal { get; init; }
    public int ProcessFailed { get; init; }
    public int ProcessSkipped { get; init; }
    public string? OpenedAs { get; init; }
    public bool Saved { get; init; }
    public string? TargetPath { get; init; }
    public string? ViewName { get; init; }
    public string? ViewKind { get; init; }
    public string? SheetNumber { get; init; }
    public bool NeedsConfirmation { get; init; }
    public string? ConfirmationText { get; init; }
    public CodeFailureKind CodeFailure { get; init; }
}

/// <summary>
/// Builds the human-readable <c>summary</c> sentence returned with every action result, and the Revit undo
/// entry name derived from it: <c>MCP (&lt;clientName&gt;): &lt;short summary&gt;</c>, at most 60 characters.
/// </summary>
public static class ActionSummaryBuilder
{
    private const int MaxGroupNameLength = 60;

    public static string BuildSummary(ActionSummaryContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (context.NeedsConfirmation) return $"Needs confirmation: {context.ConfirmationText}";
        var doc = string.IsNullOrWhiteSpace(context.DocumentTitle) ? "the model" : context.DocumentTitle;
        return context.Command switch
        {
            "select" => $"Selected {Plural(context.Count, "element")} in {doc}.",
            "show" => $"Showed {Plural(context.Count, "element")} in {doc}.",
            "isolate" => context.Count == 0
                ? $"Reset temporary isolation in {doc}."
                : $"Isolated {Plural(context.Count, "element")} in {doc}.",
            "override-graphics" => $"{(context.DryRun ? context.Reset ? "Would reset" : "Would highlight" : context.Reset ? "Reset" : "Highlighted")} {Plural(context.Count, "element")} in {doc}.",
            "move" => $"{(context.DryRun ? "Would move" : "Moved")} {Plural(context.Count, "element")} in {doc}.",
            "rotate" => $"{(context.DryRun ? "Would rotate" : "Rotated")} {Plural(context.Count, "element")} in {doc}.",
            "copy" => $"{(context.DryRun ? "Would copy" : "Copied")} {Plural(context.Count, "element")} in {doc}.",
            "mirror" => $"{(context.DryRun ? "Would mirror" : "Mirrored")} {Plural(context.Count, "element")} in {doc}.",
            "change-type" => $"{(context.DryRun ? "Would change" : "Changed")} the type of {Plural(context.Count, "element")} in {doc}.",
            "update-parameters" => $"{(context.DryRun ? "Would update" : "Updated")} parameter '{context.Parameter}' on {Plural(context.Count, "element")} in {doc}.",
            "delete" => $"{(context.DryRun ? "Would delete" : "Deleted")} {Plural(context.Count, "element")} in {doc}.",
            "place-family" => $"{(context.DryRun ? "Would place" : "Placed")} {FamilyLabel(context)} in {doc}.",
            "load-family" => $"{(context.DryRun ? "Would load" : "Loaded")} {Plural(context.Count, "family")} in {doc}.",
            "place-families" => $"{(context.DryRun ? "Would place" : "Placed")} {Plural(context.Count, "family")} in {doc}.",
            "create-wall" => $"{(context.DryRun ? "Would create" : "Created")} a{WallTypeLabel(context.WallType)} wall in {doc}.",
            "create-mep-run" => $"{(context.DryRun ? "Would create" : "Created")} {Plural(context.Count, "segment")} of {context.ViewKind?.Replace('_', ' ')} in {doc}.",
            "link-cad" => $"{(context.DryRun ? "Would " : "")}{(context.CadLink ? (context.DryRun ? "link" : "Linked") : (context.DryRun ? "import" : "Imported"))} CAD in {doc}.",
            "walls-from-cad" => $"{(context.DryRun ? "Would create" : "Created")} {Plural(context.Count, "wall")} from CAD in {doc}.",
            "create-view" => $"{(context.DryRun ? "Would create" : "Created")} {ViewKindLabel(context.ViewKind)} '{context.ViewName}' in {doc}.",
            "duplicate-view" => $"{(context.DryRun ? "Would duplicate" : "Duplicated")} view '{context.ViewName}' in {doc}.",
            "apply-view-template" => $"{(context.DryRun ? "Would apply" : "Applied")} a view template to {Plural(context.Count, "view")} in {doc}.",
            "create-sheet" => $"{(context.DryRun ? "Would create" : "Created")} sheet '{context.SheetNumber} - {context.ViewName}' in {doc}.",
            "place-views-on-sheet" => $"{(context.DryRun ? "Would place" : "Placed")} {Plural(context.Count, "view")} on sheet '{context.ViewName}' in {doc}.",
            "set-parameter" => $"{(context.DryRun ? "Would set" : "Set")} parameter '{context.Parameter}' on 1 element in {doc}.",
            "batch" => $"{(context.DryRun ? "Would run" : "Ran")} a batch of {Plural(context.BatchStepCount, "step")} in {doc}.",
            "process-models" when context.ProcessTotal > 0 => $"{(context.DryRun ? "Previewed" : "Processed")} {context.Count} of {context.ProcessTotal} models; {context.ProcessFailed} failed, {context.ProcessSkipped} skipped.",
            "process-models" => $"{(context.DryRun ? "Previewed" : "Processed")} {doc}.",
            "export-nwc" => $"Exported an NWC file from {doc}.",
            "edit-families" => $"{(context.DryRun ? "Would edit" : "Edited")} {Plural(context.Count, "family")} in {doc}.",
            "align-link-datums" => $"{(context.DryRun ? "Would align" : "Aligned")} link datums in {doc}.",
            "undo-last" => $"Requested undo of the last MCP action in {doc}.",
            "open-document" => $"Opened '{doc}'{OpenedAsLabel(context.OpenedAs)}.",
            "close-document" => context.Saved ? $"Saved and closed '{doc}'." : $"Closed '{doc}'.",
            "save-document" => context.TargetPath is null ? $"Saved '{doc}'." : $"Saved '{doc}' as {context.TargetPath}.",
            "sync-document" => $"Synchronized '{doc}' with its central model.",
            "activate-document" => $"Activated '{doc}'.",
            "activate-view" => $"Activated view '{context.ViewName}' in '{doc}'.",
            "close-views" => $"Closed {Plural(context.Count, "view")} in '{doc}'.",
            "new-document" => $"Created '{doc}'.",
            "set-view-visibility" => $"{(context.DryRun ? "Would change" : "Changed")} {Plural(context.Count, "visibility setting")} on view '{context.ViewName}' in {doc}.",
            "remove-links" => $"{(context.DryRun ? "Would remove" : "Removed")} {Plural(context.Count, "link")} in {doc}.",
            "execute-code" when context.CodeFailure == CodeFailureKind.Compilation => $"Code failed to compile in {doc}.",
            "execute-code" when context.CodeFailure == CodeFailureKind.Execution => $"Code failed in {doc}.",
            "execute-code" => $"{(context.DryRun ? "Ran a code preview" : "Executed code")} in {doc}.",
            _ => $"Ran {context.Command} in {doc}."
        };
    }

    public static string BuildGroupName(string clientName, string summary)
    {
        var name = string.IsNullOrWhiteSpace(clientName) ? "unknown" : clientName.Trim();
        var prefix = $"MCP ({name}): ";
        if (prefix.Length >= MaxGroupNameLength) return Truncate(prefix, MaxGroupNameLength);
        var available = MaxGroupNameLength - prefix.Length;
        var trimmedSummary = (summary ?? string.Empty).TrimEnd('.', ' ');
        var shortSummary = trimmedSummary.Length <= available ? trimmedSummary : Truncate(trimmedSummary, available);
        return prefix + shortSummary;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return maxLength <= 1 ? value.Substring(0, maxLength) : value.Substring(0, maxLength - 1).TrimEnd() + "…";
    }

    private static string FamilyLabel(ActionSummaryContext context) =>
        string.IsNullOrWhiteSpace(context.TypeName) ? context.Family ?? "a family instance" : $"{context.Family}: {context.TypeName}";

    private static string WallTypeLabel(string? wallType) => string.IsNullOrWhiteSpace(wallType) ? "" : $" {wallType}";

    private static string ViewKindLabel(string? kind) => kind switch
    {
        "floor_plan" => "floor plan",
        "ceiling_plan" => "ceiling plan",
        "structural_plan" => "structural plan",
        "3d" => "3D view",
        "drafting" => "drafting view",
        "section" => "section",
        _ => "view"
    };

    private static string OpenedAsLabel(string? openedAs) => string.IsNullOrWhiteSpace(openedAs) ? "" : $" ({openedAs})";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {PluralNoun(noun)}";

    private static string PluralNoun(string noun) => noun == "family" ? "families" : $"{noun}s";
}
