using Autodesk.Revit.DB;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Activity;

/// <summary>Builds one <see cref="ActivityEntry"/> per finished action job and files it in <see cref="ActivityLog"/>.</summary>
internal static class ActivityRecorder
{
    public static void RecordAction(ControlJobParseResult job, Document? document, ActionResultData? data,
        CommandResponse<ActionResultData> response, ChangeCapture? changes)
    {
        var dryRun = data?.DryRun ?? false;
        Record(new ActivityEntry
        {
            Time = DateTimeOffset.Now,
            ClientName = job.ClientName,
            Command = job.Command,
            Document = document?.Title ?? data?.Title ?? string.Empty,
            Summary = data?.Summary ?? response.Message ?? response.Error ?? string.Empty,
            DryRun = dryRun,
            UndoEntryName = data?.UndoName,
            State = ResolveState(response.Success || response.Partial, dryRun)
        }, document, data?.UndoName, data?.RolledBack == true, changes);
    }

    public static void RecordFamilyEdit(ControlJobParseResult job, Document? document, FamilyEditData? data,
        CommandResponse<FamilyEditData> response, ChangeCapture? changes)
    {
        var dryRun = data?.DryRun ?? false;
        Record(new ActivityEntry
        {
            Time = DateTimeOffset.Now,
            ClientName = job.ClientName,
            Command = job.Command,
            Document = document?.Title ?? string.Empty,
            Summary = data?.Summary ?? response.Message ?? response.Error ?? string.Empty,
            DryRun = dryRun,
            UndoEntryName = data?.UndoName,
            State = ResolveState(response.Success || response.Partial, dryRun)
        }, document, data?.UndoName, data?.RolledBack == true, changes);
    }

    private static void Record(ActivityEntry entry, Document? document, string? undoName, bool rolledBack,
        ChangeCapture? changes)
    {
        changes?.Fill(entry);
        ActivityLog.Record(entry);
        if (document is not null && undoName is { Length: > 0 })
            UndoTracker.RecordOwnCommit(document.Title, undoName);
        else if (rolledBack && changes is not null)
            // Dry runs commit inside a group and roll it back; the undo stack is where it was before the job.
            UndoTracker.Restore(changes.UndoStateAtStart);
    }

    private static string ResolveState(bool successOrPartial, bool dryRun)
    {
        if (!successOrPartial) return "failed";
        return dryRun ? "dry_run" : "done";
    }
}
