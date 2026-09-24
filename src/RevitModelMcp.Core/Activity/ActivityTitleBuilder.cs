namespace RevitModelMcp.Core.Activity;

/// <summary>
/// Builds the short row titles the activity pane shows. Finished changes read in the past tense with
/// element counts ("Moved 3 elements"); dry runs, failures and running jobs read as the process
/// ("Moving 3 elements").
/// </summary>
public static class ActivityTitleBuilder
{
    private static readonly Noun Element = new("element", "elements");
    private static readonly Noun Family = new("family", "families");
    private static readonly Noun Link = new("link", "links");

    private static readonly Dictionary<string, Phrase> Phrases = new(StringComparer.Ordinal)
    {
        ["select"] = new("Selected", "Selecting", Element),
        ["show"] = new("Showed", "Showing", Element),
        ["isolate"] = new("Isolated", "Isolating", Element),
        ["move"] = new("Moved", "Moving", Element),
        ["delete"] = new("Deleted", "Deleting", Element),
        ["edit-families"] = new("Edited", "Editing", Family),
        ["remove-links"] = new("Removed", "Removing", Link),
        ["place-family"] = new("Placed a family", "Placing a family", null),
        ["create-wall"] = new("Created a wall", "Creating a wall", null),
        ["set-parameter"] = new("Set a parameter", "Setting a parameter", null),
        ["batch"] = new("Ran a batch", "Running a batch", null),
        ["export-nwc"] = new("Exported NWC", "Exporting NWC", null),
        ["align-link-datums"] = new("Aligned datums to link", "Aligning datums to link", null),
        ["undo-last"] = new("Undid the last action", "Undoing the last action", null),
        ["open-document"] = new("Opened the document", "Opening the document", null),
        ["close-document"] = new("Closed the document", "Closing the document", null),
        ["save-document"] = new("Saved the document", "Saving the document", null),
        ["sync-document"] = new("Synchronized with central", "Synchronizing with central", null),
        ["set-view-visibility"] = new("Changed view visibility", "Changing view visibility", null)
    };

    /// <summary>
    /// Title of a recorded activity row. Uses the action's own target count when the entry has one, so a
    /// job that touches dependents as a side effect (moving a link along with the dimensions that follow
    /// it) still reads by what was asked for ("Moved 1 element"), not by everything Revit changed.
    /// </summary>
    public static string Build(ActivityEntry entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        var count = entry.ActionCount ?? entry.ChangedCount + entry.CreatedCount + entry.DeletedCount;
        var finished = !entry.DryRun && entry.State is not ("failed" or "dry_run" or "queued" or "running");
        return Format(entry.Command, count, finished);
    }

    /// <summary>Title of a job that is still running or waiting in the queue, ending with an ellipsis.</summary>
    public static string BuildRunning(string command) => Format(command, 0, false) + "…";

    private static string Format(string command, int count, bool finished)
    {
        if (!Phrases.TryGetValue(command ?? string.Empty, out var phrase))
            return finished ? $"Ran {command}" : $"Running {command}";
        var verb = finished ? phrase.Past : phrase.Process;
        if (phrase.Noun is not { } noun) return verb;
        if (count == 0) return $"{verb} {noun.Many}";
        return $"{verb} {count} {(count == 1 ? noun.One : noun.Many)}";
    }

    private sealed record Phrase(string Past, string Process, Noun? Noun);

    private sealed record Noun(string One, string Many);
}
