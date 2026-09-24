namespace RevitModelMcp.Core.Activity;

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
    public int BatchStepCount { get; init; }
    public string? OpenedAs { get; init; }
    public bool Saved { get; init; }
    public string? TargetPath { get; init; }
    public string? ViewName { get; init; }
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
        var doc = string.IsNullOrWhiteSpace(context.DocumentTitle) ? "the model" : context.DocumentTitle;
        return context.Command switch
        {
            "select" => $"Selected {Plural(context.Count, "element")} in {doc}.",
            "show" => $"Showed {Plural(context.Count, "element")} in {doc}.",
            "isolate" => context.Count == 0
                ? $"Reset temporary isolation in {doc}."
                : $"Isolated {Plural(context.Count, "element")} in {doc}.",
            "move" => $"{(context.DryRun ? "Would move" : "Moved")} {Plural(context.Count, "element")} in {doc}.",
            "delete" => $"{(context.DryRun ? "Would delete" : "Deleted")} {Plural(context.Count, "element")} in {doc}.",
            "place-family" => $"{(context.DryRun ? "Would place" : "Placed")} {FamilyLabel(context)} in {doc}.",
            "create-wall" => $"{(context.DryRun ? "Would create" : "Created")} a{WallTypeLabel(context.WallType)} wall in {doc}.",
            "set-parameter" => $"{(context.DryRun ? "Would set" : "Set")} parameter '{context.Parameter}' on 1 element in {doc}.",
            "batch" => $"{(context.DryRun ? "Would run" : "Ran")} a batch of {Plural(context.BatchStepCount, "step")} in {doc}.",
            "export-nwc" => $"Exported an NWC file from {doc}.",
            "edit-families" => $"{(context.DryRun ? "Would edit" : "Edited")} {Plural(context.Count, "family")} in {doc}.",
            "align-link-datums" => $"{(context.DryRun ? "Would align" : "Aligned")} link datums in {doc}.",
            "undo-last" => $"Requested undo of the last MCP action in {doc}.",
            "open-document" => $"Opened '{doc}'{OpenedAsLabel(context.OpenedAs)}.",
            "close-document" => context.Saved ? $"Saved and closed '{doc}'." : $"Closed '{doc}'.",
            "save-document" => context.TargetPath is null ? $"Saved '{doc}'." : $"Saved '{doc}' as {context.TargetPath}.",
            "sync-document" => $"Synchronized '{doc}' with its central model.",
            "set-view-visibility" => $"{(context.DryRun ? "Would change" : "Changed")} {Plural(context.Count, "visibility setting")} on view '{context.ViewName}' in {doc}.",
            "remove-links" => $"{(context.DryRun ? "Would remove" : "Removed")} {Plural(context.Count, "link")} in {doc}.",
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

    private static string OpenedAsLabel(string? openedAs) => string.IsNullOrWhiteSpace(openedAs) ? "" : $" ({openedAs})";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {PluralNoun(noun)}";

    private static string PluralNoun(string noun) => noun == "family" ? "families" : $"{noun}s";
}
