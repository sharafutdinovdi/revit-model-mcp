using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Batch;
using RevitModelMcp.Core.Families;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class FamilyAuditReader
{
    internal static FamilyAuditData Read(UIApplication application, Document document, IReadOnlyList<string>? names,
        string detail, Func<bool> shouldStop, out bool partial)
    {
        partial = false;
        if (document.IsFamilyDocument)
        {
            if (names is not null) throw new ArgumentException("families must be absent in family mode.");
            return new FamilyAuditData { Mode = "family", Families = [ReadFamily(document, detail: detail)] };
        }
        if (names is null) throw new ArgumentException("families is required in project mode.");
        if (document.IsModifiable) throw new InvalidOperationException("Close the project transaction before editing families.");
        var result = new FamilyAuditData { Mode = "project" };
        foreach (var (name, family) in SelectFamilies(document, names, result.Skipped))
        {
            if (shouldStop())
            {
                partial = true;
                break;
            }
            if (family is null)
            {
                result.Families.Add(new FamilyAuditFamily { Name = name, Status = "skipped", Reason = "not found" });
                continue;
            }
            var reason = SkipReason(family);
            if (reason is not null)
            {
                result.Families.Add(new FamilyAuditFamily { Name = family.Name, Status = "skipped", Reason = reason });
                continue;
            }
            using var suppression = new FamilySuppression(application, family.Name, result.Skipped);
            Document? familyDocument = null;
            try
            {
                familyDocument = document.EditFamily(family);
                result.Families.Add(ReadFamily(familyDocument, family.Name, detail));
            }
            catch (Exception exception)
            {
                PluginLog.Skipped($"family {family.Name} audit", exception);
                result.Families.Add(new FamilyAuditFamily { Name = family.Name, Status = "failed", Reason = exception.Message });
            }
            finally
            {
                try
                {
                    if (familyDocument is not null && !familyDocument.Close(false))
                        throw new InvalidOperationException("Revit did not close the audited family.");
                }
                catch (Exception exception)
                {
                    result.Skipped.Add(new SkippedRead { What = family.Name, Reason = exception.Message });
                    partial = true;
                }
            }
            if (partial) break;
        }
        partial |= result.Families.Count > 0 && shouldStop();
        if (detail == "summary")
            foreach (var family in result.Families.Where(family => family.Parameters is not null))
                family.ApplySummary();
        result.SkippedCount = result.Skipped.Count;
        return result;
    }

    internal sealed class FamilySuppression : IDisposable
    {
        private readonly UIApplication _application;
        private readonly string _familyName;
        private readonly List<SkippedRead> _skipped;
        private readonly ActionCommandExecutor.ActionFailures _failures = new();

        internal FamilySuppression(UIApplication application, string familyName, List<SkippedRead> skipped)
        {
            _application = application;
            _familyName = familyName;
            _skipped = skipped;
            application.DialogBoxShowing += OnDialog;
            application.Application.FailuresProcessing += OnFailures;
        }

        private void Record(string dialogId, string message)
        {
            _skipped.Add(new SkippedRead
            {
                What = _familyName,
                Reason = "Revit interruption suppressed during family access.",
                DialogId = dialogId,
                Message = message
            });
        }

        private void OnDialog(object? sender, DialogBoxShowingEventArgs arguments)
        {
            var message = arguments switch
            {
                TaskDialogShowingEventArgs task => task.Message,
                MessageBoxShowingEventArgs box => box.Message,
                _ => "Revit dialog did not expose a message."
            };
            var dialogType = arguments is MessageBoxShowingEventArgs messageBox ? messageBox.DialogType : 0;
            foreach (var result in BatchDialogPolicy.FamilyAuditDialogResults(arguments.GetType().Name, dialogType))
            {
                if (!arguments.OverrideResult(result)) continue;
                Record(arguments.DialogId ?? string.Empty, message);
                return;
            }
        }

        private void OnFailures(object? sender, FailuresProcessingEventArgs arguments)
        {
            var accessor = arguments.GetFailuresAccessor();
            foreach (var failure in accessor.GetFailureMessages())
                Record(failure.GetFailureDefinitionId().Guid.ToString(), failure.GetDescriptionText());
            arguments.SetProcessingResult(_failures.PreprocessFailures(accessor));
        }

        public void Dispose()
        {
            _application.DialogBoxShowing -= OnDialog;
            _application.Application.FailuresProcessing -= OnFailures;
        }
    }

    internal static List<(string Name, Family? Family)> SelectFamilies(Document project, IReadOnlyList<string> names, List<SkippedRead>? skipped = null)
    {
        var families = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>().ToList();
        if (names.Count == 1 && names[0] == "*")
        {
            var editable = families.Where(family => family.IsEditable && !family.IsInPlace)
                .OrderBy(family => family.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (editable.Count > 200 && skipped is null)
                throw new ArgumentException("The project contains more than 200 editable families; choose names explicitly.");
            skipped?.AddRange(editable.Skip(200).Select(family => new SkippedRead
            {
                What = family.Name,
                Reason = "The audit is limited to the first 200 editable families."
            }));
            return editable.Take(200).Select(family => (family.Name, (Family?)family)).ToList();
        }
        return names.Select(name => (Name: name, Family: (Family?)families.FirstOrDefault(family =>
            string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    internal static string? SkipReason(Family family) => family.IsInPlace ? "in-place" :
        !family.IsEditable ? "not editable" : null;

    internal static FamilyAuditFamily ReadFamily(Document familyDocument, string? name = null, string detail = "full")
    {
        var owner = familyDocument.OwnerFamily;
        var sharedFlag = owner?.get_Parameter(BuiltInParameter.FAMILY_SHARED);
        var candidates = FamilyPurge.Candidates(familyDocument);
        var result = new FamilyAuditFamily
        {
            Name = name ?? Path.GetFileNameWithoutExtension(familyDocument.Title),
            Category = owner?.FamilyCategory?.Name,
            IsShared = sharedFlag?.AsInteger() == 1,
            SharedFlagEditable = sharedFlag is not null && !sharedFlag.IsReadOnly,
            Parameters = ReadParameters(familyDocument, detail == "full"),
            Purgeable = FamilyPurge.Counts(familyDocument, candidates),
            PurgeableTotal = candidates.Count,
            PurgeCoverage = FamilyPurge.Coverage
        };
        if (detail == "summary") result.ApplySummary();
        return result;
    }

    internal static List<FamilyParameterData> ReadParameters(Document familyDocument, bool includeGroupName = false)
    {
        var parameters = familyDocument.FamilyManager.Parameters.Cast<FamilyParameter>().ToList();
        var unlabelableCount = 0;
        var dimensionLabels = new FilteredElementCollector(familyDocument).OfClass(typeof(Dimension))
            .Cast<Dimension>().Where(dimension => dimension.DimensionType.StyleType is
                DimensionStyleType.Linear or DimensionStyleType.LinearFixed or DimensionStyleType.Angular or
                DimensionStyleType.Radial or DimensionStyleType.Diameter or DimensionStyleType.ArcLength)
            .Select(dimension => ReadLabel(() => dimension.FamilyLabel,
                $"dimension {RevitValueReader.GetId(dimension.Id)} label", ref unlabelableCount)?.Definition.Name)
            .OfType<string>().ToList();
        var arrayLabels = new FilteredElementCollector(familyDocument).OfClass(typeof(BaseArray))
            .Cast<BaseArray>().Select(array => ReadLabel(() => array.Label, $"array {RevitValueReader.GetId(array.Id)} label", ref unlabelableCount)?.Definition.Name)
            .OfType<string>().ToList();
        if (unlabelableCount > 0) PluginLog.Debug($"{unlabelableCount} dimensions cannot be labeled");
        var usage = ParameterUsage.Evaluate(parameters.Select(parameter => new ParameterUsageInput(
            parameter.Definition.Name, parameter.IsShared,
            parameter.Definition is InternalDefinition definition && definition.BuiltInParameter != BuiltInParameter.INVALID,
            parameter.AssociatedParameters.Cast<Parameter>().Select(associated => associated.Definition.Name).ToList(),
            dimensionLabels.Where(name => name == parameter.Definition.Name).ToList(),
            arrayLabels.Where(name => name == parameter.Definition.Name).ToList(), parameter.Formula)).ToList());
        return parameters.Select(parameter =>
            {
                var used = usage[parameter.Definition.Name];
                return new FamilyParameterData
                {
                    Name = parameter.Definition.Name,
                    IsInstance = parameter.IsInstance,
                    IsShared = parameter.IsShared,
                    Guid = parameter.IsShared ? parameter.GUID.ToString() : null,
                    Group = parameter.Definition.GetGroupTypeId().TypeId,
                    GroupName = includeGroupName ? ReadGroupName(parameter) : null,
                    Formula = parameter.Formula,
                    IsReporting = parameter.IsReporting,
                    Used = used.Used,
                    UsedBy = used.UsedBy.ToList(),
                    BuiltIn = parameter.Definition is InternalDefinition definition && definition.BuiltInParameter != BuiltInParameter.INVALID,
                    DataCarrierRisk = used.DataCarrierRisk
                };
            }).ToList();
    }

    private static string? ReadGroupName(FamilyParameter parameter)
    {
        try
        {
            return LabelUtils.GetLabelForGroup(parameter.Definition.GetGroupTypeId());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FamilyParameter? ReadLabel(Func<FamilyParameter?> getLabel, string what, ref int unlabelableCount)
    {
        try
        {
            return getLabel();
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException exception)
            when (exception.Message.IndexOf("can not be labeled", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            unlabelableCount++;
            return null;
        }
        catch (Exception exception)
        {
            PluginLog.Skipped(what, exception);
            return null;
        }
    }
}
