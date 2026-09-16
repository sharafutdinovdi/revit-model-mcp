using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class ActionVerifier
{
    internal static ActionFacts? CaptureBefore(Document targetDocument, string command,
        ActionJobContract action, List<ElementId> ids) => command switch
        {
            "move" => new ActionFacts { Elements = ids.Select(id => Bounds(RequiredElement(targetDocument, RevitValueReader.GetId(id)))).ToList() },
            "set-parameter" => ParameterFacts(targetDocument, action),
            "delete" => new ActionFacts { Requested = ids.Select(RevitValueReader.GetId).ToList() },
            _ => null
        };

    internal static void CaptureAfter(Document targetDocument, string command, ActionJobContract action, ActionResultData result)
    {
        var verification = result.Verification!;
        switch (command)
        {
            case "move":
                var after = action.ElementIds.Select(id => Bounds(RequiredElement(targetDocument, id))).ToList();
                verification.After = new ActionFacts { Elements = after };
                verification.Changed = verification.Before!.Elements!.Zip(after, (before, current) =>
                    SameBounds(before.BoundingBoxMinMm, current.BoundingBoxMinMm) &&
                    SameBounds(before.BoundingBoxMaxMm, current.BoundingBoxMaxMm) ? (long?)null : current.Id)
                    .Where(id => id.HasValue).Select(id => id!.Value).ToList();
                break;
            case "set-parameter":
                verification.After = ParameterFacts(targetDocument, action);
                verification.Changed = verification.Before!.Value == verification.After.Value ? [] : [action.ElementId];
                break;
            case "place-family":
            case "create-wall":
                var element = RequiredElement(targetDocument, result.Id!.Value);
                var facts = Bounds(element);
                var type = element.GetTypeId().ToElement<ElementType>(targetDocument);
                facts.Family = type?.FamilyName ?? string.Empty;
                facts.Type = type?.Name ?? string.Empty;
                facts.Level = element.LevelId.ToElement<Level>(targetDocument)?.Name ?? string.Empty;
                verification.After = facts;
                verification.WouldCreate = action.DryRun ? true : null;
                break;
            case "delete":
                var stillPresent = verification.Changed!
                    .Where(id => ActionCommandExecutor.CreateId(id).ToElement(targetDocument) is not null).ToList();
                verification.After = new ActionFacts { StillPresent = stillPresent };
                if (stillPresent.Count > 0) throw new InvalidOperationException("Deletion verification found surviving elements.");
                break;
        }
    }

    private static Element RequiredElement(Document targetDocument, long id) =>
        ActionCommandExecutor.CreateId(id).ToElement(targetDocument)
        ?? throw new InvalidOperationException($"Verification could not find element {id}.");

    private static ActionFacts ParameterFacts(Document targetDocument, ActionJobContract action)
    {
        var element = RequiredElement(targetDocument, action.ElementId);
        var parameter = element.FindParameter(action.Parameter!)
                        ?? throw new ArgumentException($"Parameter '{action.Parameter}' was not found on the instance or type.");
        return new ActionFacts
        {
            Id = RevitValueReader.GetId(element.Id),
            Parameter = parameter.Definition.Name,
            Value = ActionMutations.ParameterValue(parameter),
            StorageType = parameter.StorageType.ToString(),
            Owner = parameter.Element.Id == element.Id ? "instance" : "type"
        };
    }

    private static ActionFacts Bounds(Element element)
    {
        using var bounds = element.get_BoundingBox(null);
        return new ActionFacts
        {
            Id = RevitValueReader.GetId(element.Id),
            Category = element.Category?.Name ?? string.Empty,
            BoundingBoxMinMm = bounds is null ? null : Millimeters(bounds.Min),
            BoundingBoxMaxMm = bounds is null ? null : Millimeters(bounds.Max)
        };
    }

    private static List<double> Millimeters(XYZ point) =>
        new[] { point.X, point.Y, point.Z }
            .Select(value => Math.Round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters), 1)).ToList();

    private static bool SameBounds(List<double>? before, List<double>? after) =>
        before is null ? after is null : after is not null && before.SequenceEqual(after);
}
