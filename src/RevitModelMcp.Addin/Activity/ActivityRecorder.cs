using Autodesk.Revit.DB;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Activity;

/// <summary>Builds one <see cref="ActivityEntry"/> per finished action job and files it in <see cref="ActivityLog"/>.</summary>
internal static class ActivityRecorder
{
    public static void RecordSystemNotice(string message, string? releaseUrl = null, bool failed = false)
    {
        ActivityLog.Record(new ActivityEntry
        {
            Time = DateTimeOffset.Now,
            ClientName = "System",
            Command = "system-notice",
            Summary = message,
            ReleaseUrl = releaseUrl,
            State = failed ? "failed" : "done"
        });
    }

    public static void RecordAction(ControlJobParseResult job, Document? document, ActionResultData? data,
        CommandResponse<ActionResultData> response, ChangeCapture? changes)
    {
        var dryRun = data?.DryRun ?? false;
        var entry = new ActivityEntry
        {
            Time = DateTimeOffset.Now,
            ClientName = job.ClientName,
            Command = job.Command,
            Document = document?.Title ?? data?.Title ?? string.Empty,
            Summary = data?.Summary ?? response.Message ?? response.Error ?? string.Empty,
            DryRun = dryRun,
            UndoEntryName = data?.UndoName,
            ActionCount = data?.Count,
            State = ResolveState(response.Success || response.Partial, dryRun)
        };
        Record(entry, document, data?.UndoName, data?.RolledBack == true, changes,
            dryRun && document is not null && data is not null
                ? () => FillDryRun(entry, document, data) : null);
    }

    public static void RecordFamilyEdit(ControlJobParseResult job, Document? document, FamilyEditData? data,
        CommandResponse<FamilyEditData> response, ChangeCapture? changes)
    {
        var dryRun = data?.DryRun ?? false;
        var entry = new ActivityEntry
        {
            Time = DateTimeOffset.Now,
            ClientName = job.ClientName,
            Command = job.Command,
            Document = document?.Title ?? string.Empty,
            Summary = data?.Summary ?? response.Message ?? response.Error ?? string.Empty,
            DryRun = dryRun,
            UndoEntryName = data?.UndoName,
            ActionCount = data?.Families.Count(family => family.Status != "skipped"),
            State = ResolveState(response.Success || response.Partial, dryRun)
        };
        Record(entry, document, data?.UndoName, data?.RolledBack == true, changes,
            dryRun && document is not null && data is not null
                ? () => FillFamilyDryRun(entry, document, data) : null);
    }

    private static void Record(ActivityEntry entry, Document? document, string? undoName, bool rolledBack,
        ChangeCapture? changes, Action? fillDryRun = null)
    {
        if (fillDryRun is not null) fillDryRun();
        else changes?.Fill(entry);
        ActivityLog.Record(entry);
        if (document is not null && undoName is { Length: > 0 })
            UndoTracker.RecordOwnCommit(document.Title, undoName);
        else if (rolledBack && changes is not null)
            // A rolled-back job leaves the undo stack where it was before the job.
            UndoTracker.Restore(changes.UndoStateAtStart);
    }

    private static void FillDryRun(ActivityEntry entry, Document document, ActionResultData data)
    {
        var changed = new Dictionary<long, ActivityElementRef>();
        var created = new Dictionary<long, ActivityElementRef>();

        void Add(long id, bool isCreated = false, string? category = null, string? name = null)
        {
            var element = document.GetElement(ActionCommandExecutor.CreateId(id));
            var reference = new ActivityElementRef
            {
                Id = id,
                Category = element?.Category?.Name ?? category,
                Name = element?.Name ?? name
            };
            if (isCreated) created[id] = reference;
            else changed[id] = reference;
        }

        void AddResult(ActionResultData result)
        {
            if (result.Steps is not null)
            {
                foreach (var step in result.Steps)
                    if (step.Data is not null)
                        AddResult(step.Data);
                return;
            }
            foreach (var id in result.Verification?.Changed ?? []) Add(id);
            if (result.Id is long createdId) Add(createdId, isCreated: true, category: result.Category);
            if (result.Items is not null)
                foreach (var item in result.Items.Where(item => item.HostId.HasValue && item.Status is ("moved" or "created")))
                {
                    Add(item.HostId!.Value, item.Status == "created", item.Kind, item.Name);
                    if (item.PlanViewId is long planId) Add(planId, isCreated: true, category: "View");
                }
            if (result.Visibility is { Changes.Count: > 0 } visibility)
                Add(visibility.ViewId, visibility.Changes.Any(change => change.Setting == "duplicatedFrom"),
                    "View", visibility.ViewName);
            foreach (var link in result.LinkRemoval?.Removed ?? []) Add(link.Id, category: link.Kind, name: link.Name);
        }

        AddResult(data);
        entry.ChangedTotal = changed.Count;
        entry.CreatedTotal = created.Count;
        entry.Changed = changed.Values.Take(ChangeCapture.MaxRefs).ToList();
        entry.Created = created.Values.Take(ChangeCapture.MaxRefs).ToList();
    }

    private static void FillFamilyDryRun(ActivityEntry entry, Document document, FamilyEditData data)
    {
        if (document.IsFamilyDocument) return;
        var names = data.Families.Where(family => family.Status == "changed")
            .Select(family => family.Name).ToHashSet(StringComparer.Ordinal);
        using var families = new FilteredElementCollector(document).OfClass(typeof(Family));
        var references = families.Cast<Family>().Where(family => names.Contains(family.Name))
            .Select(family => new ActivityElementRef
            {
                Id = RevitModelMcp.Capture.RevitValueReader.GetId(family.Id),
                Category = family.FamilyCategory?.Name,
                Name = family.Name
            }).ToList();
        entry.ChangedTotal = references.Count;
        entry.Changed = references.Take(ChangeCapture.MaxRefs).ToList();
    }

    private static string ResolveState(bool successOrPartial, bool dryRun)
    {
        if (!successOrPartial) return "failed";
        return dryRun ? "dry_run" : "done";
    }
}
