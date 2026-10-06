using System.IO;
using Autodesk.Revit.DB;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class LinkRemoval
{
    internal const string UndoWarning = "Removing links cannot be undone in Revit; Undo will not restore them.";

    public static ActionResultData Execute(Document document, LinkRemovalOptions options, bool dryRun,
        ActionCommandExecutor.ActionFailures failures, string clientName, string? confirmToken)
    {
        var local = IsLocalCopy(document);
        if (document.IsWorkshared && !document.IsDetached && !local)
            throw new InvalidOperationException("Link removal is refused on a central-connected workshared document. Open a detached document or local copy.");
        var result = new LinkRemovalResult { Warning = dryRun ? null : UndoWarning };
        if (document.IsWorkshared && local && !document.IsDetached)
            result.Warning = (result.Warning is null ? "" : result.Warning + " ") +
                "Synchronizing this local copy would propagate link removal to the central model.";
        var candidates = new List<(Element Element, string Kind, int InstanceCount)>();
        if (options.Kinds.Contains("revit"))
        {
            using var collector = new FilteredElementCollector(document).OfClass(typeof(RevitLinkType));
            candidates.AddRange(collector.Cast<RevitLinkType>().Select(type => ((Element)type, "revit", Count<RevitLinkInstance>(document, type.Id))));
        }
        if (options.Kinds.Contains("cad"))
        {
            using var collector = new FilteredElementCollector(document).OfClass(typeof(CADLinkType));
            foreach (var type in collector.Cast<CADLinkType>())
            {
                using var instances = new FilteredElementCollector(document).OfClass(typeof(ImportInstance));
                var owned = instances.Cast<ImportInstance>().Where(instance => instance.GetTypeId() == type.Id).ToList();
                if (!options.IncludeImportedCad && !type.IsExternalFileReference() && owned.All(instance => !instance.IsLinked)) continue;
                candidates.Add((type, "cad", owned.Count));
            }
        }
        if (options.Kinds.Contains("point_cloud"))
        {
            using var collector = new FilteredElementCollector(document).OfClass(typeof(PointCloudType));
            candidates.AddRange(collector.Cast<PointCloudType>().Select(type => ((Element)type, "point_cloud", Count<PointCloudInstance>(document, type.Id))));
        }
        if (options.Kinds.Contains("image"))
        {
            using var collector = new FilteredElementCollector(document).OfClass(typeof(ImageType));
            candidates.AddRange(collector.Cast<ImageType>().Select(type => ((Element)type, "image", Count<ImageInstance>(document, type.Id))));
        }
        var selected = options.Links.Contains("*") ? candidates : candidates.Where(candidate =>
            options.Links.Any(reference => string.Equals(reference, candidate.Element.Name, StringComparison.OrdinalIgnoreCase)
                || long.TryParse(reference, out var id) && id == RevitValueReader.GetId(candidate.Element.Id))).ToList();
        foreach (var reference in options.Links.Where(reference => reference != "*"))
            if (!selected.Any(candidate => string.Equals(reference, candidate.Element.Name, StringComparison.OrdinalIgnoreCase)
                || long.TryParse(reference, out var id) && id == RevitValueReader.GetId(candidate.Element.Id)))
                throw new ArgumentException($"Link '{reference}' was not found among the selected kinds.");
        if (!dryRun)
        {
            var text = "Remove these links: " + string.Join("\n", selected.Select(candidate =>
                $"{candidate.Kind}: {candidate.Element.Name} (id {RevitValueReader.GetId(candidate.Element.Id)}, {candidate.InstanceCount} instances)")) +
                "\n" + result.Warning;
            var gate = ConfirmationStore.Gate("remove-links",
                DocumentConfirmationBinding.Identity(document.PathName, document.Title),
                DocumentConfirmationBinding.LinkRemovalArguments(selected.Select(candidate =>
                    (candidate.Kind, RevitValueReader.GetId(candidate.Element.Id), candidate.Element.Name))),
                ConfirmationStore.State(document), confirmToken, text,
                $"Needs confirmation to remove {selected.Count} links from {document.Title}.",
                "The document changed after the preview. The confirmation token is used up; repeat the call without confirm_token to get a new one.");
            if (gate is not null)
            {
                gate.Warning = UndoWarning;
                return gate;
            }
        }
        using var group = new TransactionGroup(document, "MCP action");
        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the action transaction group.");
        using var transaction = new Transaction(document, "revit_remove_links");
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the link removal transaction.");
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        try
        {
            var removedIds = new List<long>();
            foreach (var candidate in selected)
            {
                var record = new RemovedLink
                {
                    Id = RevitValueReader.GetId(candidate.Element.Id),
                    Name = candidate.Element.Name,
                    Kind = candidate.Kind,
                    InstanceCount = candidate.InstanceCount
                };
                removedIds.AddRange(document.Delete(candidate.Element.Id).Select(RevitValueReader.GetId));
                result.Removed.Add(record);
            }
            var humanSummary = ActionSummaryBuilder.BuildSummary(new ActionSummaryContext
            {
                Command = "remove-links",
                DocumentTitle = document.Title,
                Count = result.Removed.Count,
                DryRun = dryRun
            });
            var groupName = ActionSummaryBuilder.BuildGroupName(clientName, humanSummary);
            if (dryRun)
            {
                // Dry runs must never commit: some deletions are irreversible.
                if (transaction.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException(failures.Message ?? "Could not roll back the dry run.");
                group.RollBack();
            }
            else
            {
                transaction.SetName(groupName);
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException(failures.Message ?? "Link removal transaction failed.");
                group.SetName(groupName);
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Could not assimilate the action transaction group.");
            }
            return new ActionResultData
            {
                LinkRemoval = result,
                Warning = result.Warning,
                Verification = dryRun ? new ActionVerification { Changed = removedIds.Distinct().ToList() } : null,
                DryRun = dryRun,
                RolledBack = dryRun,
                Committed = !dryRun,
                Summary = humanSummary,
                UndoName = dryRun ? null : groupName
            };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    private static int Count<T>(Document document, ElementId typeId) where T : Element
    {
        using var collector = new FilteredElementCollector(document).OfClass(typeof(T));
        return collector.Cast<T>().Count(instance => instance.GetTypeId() == typeId);
    }

    private static bool IsLocalCopy(Document document)
    {
        if (!document.IsWorkshared || document.IsDetached || string.IsNullOrEmpty(document.PathName) || !File.Exists(document.PathName))
            return false;
        using var info = BasicFileInfo.Extract(document.PathName);
        return !info.IsCentral;
    }
}
