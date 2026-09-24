using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Activity;

/// <summary>Builds one <see cref="ActivityEntry"/> per finished action job and files it in <see cref="ActivityLog"/>.</summary>
internal static class ActivityRecorder
{
    public static void RecordAction(ControlJobParseResult job, Document? document, ActionResultData? data,
        CommandResponse<ActionResultData> response)
    {
        var entry = new ActivityEntry
        {
            Time = DateTimeOffset.Now,
            ClientName = job.ClientName,
            Command = job.Command,
            Document = document?.Title ?? data?.Title ?? string.Empty,
            Summary = data?.Summary ?? response.Message ?? response.Error ?? string.Empty,
            DryRun = data?.DryRun ?? false,
            UndoEntryName = data?.UndoName,
            State = ResolveState(response.Success || response.Partial, data)
        };
        if (document is not null && data is not null)
        {
            var (changed, created, deleted) = ExtractElementRefs(document, job.Command, data);
            entry.Changed = changed;
            entry.Created = created;
            entry.Deleted = deleted;
        }
        ActivityLog.Record(entry);
    }

    public static void RecordFamilyEdit(ControlJobParseResult job, Document? document, FamilyEditData? data,
        CommandResponse<FamilyEditData> response)
    {
        var entry = new ActivityEntry
        {
            Time = DateTimeOffset.Now,
            ClientName = job.ClientName,
            Command = job.Command,
            Document = document?.Title ?? string.Empty,
            Summary = data?.Summary ?? response.Message ?? response.Error ?? string.Empty,
            DryRun = data?.DryRun ?? false,
            UndoEntryName = data?.UndoName,
            State = ResolveState(response.Success || response.Partial, data is null ? null : new ActionResultData { DryRun = data.DryRun })
        };
        ActivityLog.Record(entry);
    }

    private static string ResolveState(bool successOrPartial, ActionResultData? data)
    {
        if (!successOrPartial) return "failed";
        return data?.DryRun == true ? "dry_run" : "done";
    }

    private static (List<ActivityElementRef> Changed, List<ActivityElementRef> Created, List<ActivityElementRef> Deleted)
        ExtractElementRefs(Document document, string command, ActionResultData data)
    {
        List<ActivityElementRef> FromIds(IEnumerable<long>? ids, bool existing) =>
            (ids ?? []).Select(id => Describe(document, id, existing)).ToList();

        return command switch
        {
            "delete" => ([], [], FromIds(data.Verification?.Changed, false)),
            "place-family" or "create-wall" => ([], FromIds(data.Id.HasValue ? [data.Id.Value] : null, true), []),
            "move" or "set-parameter" => (FromIds(data.Verification?.Changed, true), [], []),
            "batch" => AggregateBatch(document, data.Steps),
            _ => ([], [], [])
        };
    }

    private static (List<ActivityElementRef>, List<ActivityElementRef>, List<ActivityElementRef>) AggregateBatch(
        Document document, List<BatchStepResult>? steps)
    {
        var changed = new List<ActivityElementRef>();
        var created = new List<ActivityElementRef>();
        var deleted = new List<ActivityElementRef>();
        foreach (var step in steps ?? [])
        {
            if (step.Data is null || !step.Success) continue;
            var (stepChanged, stepCreated, stepDeleted) = ExtractElementRefs(document, step.Command, step.Data);
            changed.AddRange(stepChanged);
            created.AddRange(stepCreated);
            deleted.AddRange(stepDeleted);
        }
        return (changed, created, deleted);
    }

    private static ActivityElementRef Describe(Document document, long id, bool existing)
    {
        if (!existing) return new ActivityElementRef { Id = id };
        var element = ActionCommandExecutor.CreateId(id).ToElement(document);
        return new ActivityElementRef { Id = id, Category = element?.Category?.Name, Name = element?.Name };
    }
}
