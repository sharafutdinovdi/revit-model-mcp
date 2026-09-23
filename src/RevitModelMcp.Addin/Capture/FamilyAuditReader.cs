using Autodesk.Revit.DB;
using RevitModelMcp.Control;
using RevitModelMcp.Core.Families;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class FamilyAuditReader
{
    internal static FamilyAuditData Read(Document document, IReadOnlyList<string>? names)
    {
        if (document.IsFamilyDocument)
        {
            if (names is not null) throw new ArgumentException("families must be absent in family mode.");
            return new FamilyAuditData { Mode = "family", Families = [ReadFamily(document)] };
        }
        if (names is null) throw new ArgumentException("families is required in project mode.");
        if (document.IsModifiable) throw new InvalidOperationException("Close the project transaction before editing families.");
        var result = new FamilyAuditData { Mode = "project" };
        foreach (var (name, family) in SelectFamilies(document, names))
        {
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
            Document? familyDocument = null;
            try
            {
                familyDocument = document.EditFamily(family);
                result.Families.Add(ReadFamily(familyDocument));
            }
            catch (Exception exception)
            {
                result.Families.Add(new FamilyAuditFamily { Name = family.Name, Status = "failed", Reason = exception.Message });
            }
            finally
            {
                familyDocument?.Close(false);
            }
        }
        return result;
    }

    internal static List<(string Name, Family? Family)> SelectFamilies(Document project, IReadOnlyList<string> names)
    {
        var families = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>().ToList();
        if (names.Count == 1 && names[0] == "*")
        {
            var editable = families.Where(family => family.IsEditable && !family.IsInPlace).ToList();
            if (editable.Count > 200) throw new ArgumentException("The project contains more than 200 editable families; choose names explicitly.");
            return editable.Select(family => (family.Name, (Family?)family)).ToList();
        }
        return names.Select(name => (Name: name, Family: (Family?)families.FirstOrDefault(family =>
            string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    internal static string? SkipReason(Family family) => family.IsInPlace ? "in-place" :
        !family.IsEditable ? "not editable" : null;

    internal static FamilyAuditFamily ReadFamily(Document familyDocument)
    {
        var owner = familyDocument.OwnerFamily;
        var sharedFlag = owner?.get_Parameter(BuiltInParameter.FAMILY_SHARED);
        var candidates = FamilyPurge.Candidates(familyDocument);
        return new FamilyAuditFamily
        {
            Name = owner?.Name ?? familyDocument.Title,
            Category = owner?.FamilyCategory?.Name,
            IsShared = sharedFlag?.AsInteger() == 1,
            SharedFlagEditable = sharedFlag is not null && !sharedFlag.IsReadOnly,
            Parameters = ReadParameters(familyDocument),
            Purgeable = FamilyPurge.Counts(familyDocument, candidates),
            PurgeableTotal = candidates.Count,
            PurgeCoverage = FamilyPurge.Coverage
        };
    }

    internal static List<FamilyParameterData> ReadParameters(Document familyDocument)
    {
        var parameters = familyDocument.FamilyManager.Parameters.Cast<FamilyParameter>().ToList();
        var dimensionLabels = new FilteredElementCollector(familyDocument).OfClass(typeof(Dimension))
            .Cast<Dimension>().Select(dimension => ReadLabel(() => dimension.FamilyLabel)?.Definition.Name)
            .OfType<string>().ToList();
        var arrayLabels = new FilteredElementCollector(familyDocument).OfClass(typeof(BaseArray))
            .Cast<BaseArray>().Select(array => ReadLabel(() => array.Label)?.Definition.Name)
            .OfType<string>().ToList();
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
                    Formula = parameter.Formula,
                    IsReporting = parameter.IsReporting,
                    Used = used.Used,
                    UsedBy = used.UsedBy.ToList(),
                    BuiltIn = parameter.Definition is InternalDefinition definition && definition.BuiltInParameter != BuiltInParameter.INVALID,
                    DataCarrierRisk = used.DataCarrierRisk
                };
            }).ToList();
    }

    private static FamilyParameter? ReadLabel(Func<FamilyParameter?> getLabel)
    {
        try
        {
            return getLabel();
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return null;
        }
    }
}
