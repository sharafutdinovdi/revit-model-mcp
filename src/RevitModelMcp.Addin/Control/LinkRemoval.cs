using System.IO;
using Autodesk.Revit.DB;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class LinkRemoval
{
    public static ActionResultData Execute(Document document, LinkRemovalOptions options, bool dryRun,
        ActionCommandExecutor.ActionFailures failures)
    {
        var local = IsLocalCopy(document);
        if (document.IsWorkshared && !document.IsDetached && !local)
            throw new InvalidOperationException("Link removal is refused on a central-connected workshared document. Open a detached document or local copy.");
        var result = new LinkRemovalResult();
        if (document.IsWorkshared && local && !document.IsDetached)
            result.Warning = "Synchronizing this local copy would propagate link removal to the central model.";
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
        using var transaction = new Transaction(document, "revit_remove_links");
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the link removal transaction.");
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        try
        {
            foreach (var candidate in selected)
            {
                var record = new RemovedLink
                {
                    Id = RevitValueReader.GetId(candidate.Element.Id), Name = candidate.Element.Name,
                    Kind = candidate.Kind, InstanceCount = candidate.InstanceCount
                };
                document.Delete(candidate.Element.Id);
                result.Removed.Add(record);
            }
            var status = dryRun ? transaction.RollBack() : transaction.Commit();
            if (status != (dryRun ? TransactionStatus.RolledBack : TransactionStatus.Committed))
                throw new InvalidOperationException(failures.Message ?? "Link removal transaction failed.");
            return new ActionResultData { LinkRemoval = result, DryRun = dryRun, RolledBack = dryRun, Committed = !dryRun };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
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
