using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Nice3point.Revit.Toolkit.Options;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Control;

internal static class FamilyEditor
{
    internal static FamilyEditData Execute(Document document, ActionJobContract job,
        ActionCommandExecutor.ActionFailures failures, Autodesk.Revit.ApplicationServices.Application application)
    {
        if (document.IsFamilyDocument)
        {
            if (job.Families is not null) throw new ArgumentException("families must be absent in family mode.");
            var result = new FamilyEditData { Mode = "family", DryRun = job.DryRun };
            result.Families.Add(Edit(document, document.OwnerFamily?.Name ?? document.Title, job, failures, application));
            result.Committed = !job.DryRun && result.Families[0].Status != "failed";
            result.FailedFamily = result.Families[0].Status == "failed" ? result.Families[0].Name : null;
            return result;
        }
        if (job.Families is null) throw new ArgumentException("families is required in project mode.");
        if (document.IsModifiable) throw new InvalidOperationException("Close the project transaction before editing families.");
        var data = new FamilyEditData { Mode = "project", DryRun = job.DryRun };
        using var group = new TransactionGroup(document, "revit_edit_families");
        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the family edit group.");
        try
        {
            foreach (var (name, family) in FamilyAuditReader.SelectFamilies(document, job.Families))
            {
                if (family is null)
                {
                    data.Families.Add(new FamilyEditFamily { Name = name, Status = "skipped", Reason = "not found" });
                    continue;
                }
                var reason = FamilyAuditReader.SkipReason(family);
                if (reason is null && document.IsWorkshared &&
                    WorksharingUtils.GetCheckoutStatus(document, family.Id, out var owner) == CheckoutStatus.OwnedByOtherUser)
                    reason = $"owned by {owner}";
                if (reason is not null)
                {
                    data.Families.Add(new FamilyEditFamily { Name = family.Name, Status = "skipped", Reason = reason });
                    continue;
                }
                using var familyGroup = new TransactionGroup(document, "revit_edit_families");
                if (familyGroup.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException($"Could not start the edit group for '{family.Name}'.");
                Document? familyDocument = null;
                FamilyEditFamily familyResult;
                try
                {
                    familyDocument = document.EditFamily(family);
                    familyResult = Edit(familyDocument, family.Name, job, failures, application);
                    if (familyResult.Status == "changed" && !job.DryRun)
                    {
                        var loadedFamily = familyDocument.LoadFamily(document,
                            new FamilyLoadOptions(job.OverwriteParameterValues, FamilySource.Project));
                        familyResult.Loaded = true;
                        var requestedShared = job.Operations.LastOrDefault(operation => operation.Op == "set_shared")?.Shared;
                        if (requestedShared.HasValue &&
                            loadedFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() !=
                            (requestedShared.Value ? 1 : 0))
                        {
                            familyResult.Status = "failed";
                            familyResult.Reason = "Revit kept the previous shared state; delete and reload the family manually";
                        }
                    }
                }
                catch (Exception exception)
                {
                    familyResult = new FamilyEditFamily { Name = family.Name, Status = "failed", Reason = exception.Message };
                }
                finally
                {
                    familyDocument?.Close(false);
                }
                if (familyResult.Status == "failed" || job.DryRun)
                {
                    familyGroup.RollBack();
                    familyResult.RolledBack = true;
                }
                else if (familyGroup.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException($"Could not commit the edit group for '{family.Name}'.");
                data.Families.Add(familyResult);
                if (familyResult.Status == "failed")
                {
                    data.FailedFamily ??= family.Name;
                    if (job.StopOnError) break;
                }
            }
            if (job.DryRun || data.FailedFamily is not null && job.StopOnError)
            {
                group.RollBack();
                data.RolledBack = true;
                foreach (var family in data.Families) family.RolledBack = true;
            }
            else
            {
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Could not commit the family edits.");
                data.Committed = true;
            }
            return data;
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    private static FamilyEditFamily Edit(Document familyDocument, string name, ActionJobContract job,
        ActionCommandExecutor.ActionFailures failures, Autodesk.Revit.ApplicationServices.Application application)
    {
        var result = new FamilyEditFamily { Name = name, Status = "unchanged" };
        using var transaction = new Transaction(familyDocument, "revit_edit_families");
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the family transaction.");
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        try
        {
            foreach (var operation in job.Operations)
            {
                var operationResult = operation.Op switch
                {
                    "add_shared_parameters" => AddShared(familyDocument, operation, application),
                    "remove_parameters" => Remove(familyDocument, operation),
                    "purge" => FamilyPurge.Execute(familyDocument),
                    "set_shared" => SetShared(familyDocument, operation),
                    _ => throw new ArgumentException($"Unknown family operation: {operation.Op}.")
                };
                result.Operations.Add(operationResult);
                if (operationResult.Op == "purge" && operationResult.Deleted?.Values.Sum() > 0 ||
                    operationResult.Status == "changed" ||
                    operationResult.Results?.Any(item => item.Status is "added" or "replaced" or "removed") == true)
                    result.Status = "changed";
            }
            familyDocument.Regenerate();
            if (job.DryRun)
            {
                _ = FamilyAuditReader.ReadFamily(familyDocument);
                transaction.RollBack();
            }
            else if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException(failures.Message ?? "The family transaction was rolled back.");
            return result;
        }
        catch (Exception exception)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            result.Status = "failed";
            result.Reason = exception.Message;
            return result;
        }
    }

    private static FamilyOperationResult AddShared(Document document, FamilyEditOperationContract operation,
        Autodesk.Revit.ApplicationServices.Application application)
    {
        var originalFile = application.SharedParametersFilename;
        var path = operation.SharedParameterFile ?? originalFile;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
            throw new FileNotFoundException("Shared parameter file is missing or unreadable.", path);
        var result = new FamilyOperationResult { Op = operation.Op!, Results = [] };
        try
        {
            application.SharedParametersFilename = path;
            var file = application.OpenSharedParameterFile()
                ?? throw new InvalidOperationException("Cannot open the shared parameter file.");
            foreach (var requested in operation.Parameters!)
            {
                var definitions = file.Groups.Cast<DefinitionGroup>()
                    .SelectMany(group => group.Definitions.Cast<Definition>()
                        .OfType<ExternalDefinition>().Select(definition => (Group: group.Name, Definition: definition)))
                    .Where(item => requested.Guid is not null
                        ? item.Definition.GUID == Guid.Parse(requested.Guid)
                        : string.Equals(item.Definition.Name, requested.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (definitions.Count == 0) throw new ArgumentException($"Shared parameter '{requested.Name}' was not found.");
                if (definitions.Count > 1) throw new ArgumentException(
                    $"Shared parameter '{requested.Name}' is ambiguous in groups: {string.Join(", ", definitions.Select(item => item.Group))}.");
                var definition = definitions[0].Definition;
                var property = typeof(GroupTypeId).GetProperty(requested.Group!, BindingFlags.Public | BindingFlags.Static);
                if (property?.GetValue(null) is not ForgeTypeId groupType)
                    throw new ArgumentException($"Unknown parameter group: {requested.Group}.");
                var existing = document.FamilyManager.Parameters.Cast<FamilyParameter>()
                    .FirstOrDefault(parameter => parameter.IsShared && parameter.GUID == definition.GUID);
                var item = new FamilyItemResult { Name = requested.Name!, Guid = definition.GUID.ToString() };
                if (existing is not null) item.Status = "exists";
                else
                {
                    existing = document.FamilyManager.Parameters.Cast<FamilyParameter>()
                        .FirstOrDefault(parameter => string.Equals(parameter.Definition.Name, requested.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null && !operation.ReplaceFamilyParameter) item.Status = "conflict";
                    else if (existing is not null)
                    {
                        document.FamilyManager.ReplaceParameter(existing, definition, groupType, requested.Instance);
                        item.Status = "replaced";
                    }
                    else
                    {
                        document.FamilyManager.AddParameter(definition, groupType, requested.Instance);
                        item.Status = "added";
                    }
                }
                result.Results.Add(item);
            }
        }
        finally
        {
            application.SharedParametersFilename = originalFile;
        }
        return result;
    }

    private static FamilyOperationResult Remove(Document document, FamilyEditOperationContract operation)
    {
        var result = new FamilyOperationResult { Op = operation.Op!, Results = [] };
        var parameters = FamilyAuditReader.ReadParameters(document);
        foreach (var name in operation.Names!)
        {
            var parameter = document.FamilyManager.Parameters.Cast<FamilyParameter>()
                .FirstOrDefault(candidate => string.Equals(candidate.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
            var item = new FamilyItemResult { Name = name };
            if (parameter is null) item.Status = "not_found";
            else
            {
                var audit = parameters.First(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (audit.BuiltIn) { item.Status = "kept"; item.Reason = "built-in"; }
                else if (parameter.IsShared && !operation.IncludeShared)
                { item.Status = "kept"; item.Reason = "shared parameter; pass include_shared to remove"; }
                else if (audit.Used)
                { item.Status = "kept"; item.UsedBy = audit.UsedBy; }
                else
                { document.FamilyManager.RemoveParameter(parameter); item.Status = "removed"; }
            }
            result.Results.Add(item);
        }
        return result;
    }

    private static FamilyOperationResult SetShared(Document document, FamilyEditOperationContract operation)
    {
        var result = new FamilyOperationResult { Op = "set_shared" };
        var parameter = document.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED);
        if (parameter is null || parameter.IsReadOnly)
        { result.Status = "unsupported"; return result; }
        result.Before = parameter.AsInteger() == 1;
        result.After = operation.Shared;
        result.Status = result.Before == result.After ? "unchanged" : "changed";
        if (result.Status == "changed") parameter.Set(operation.Shared!.Value ? 1 : 0);
        return result;
    }
}
