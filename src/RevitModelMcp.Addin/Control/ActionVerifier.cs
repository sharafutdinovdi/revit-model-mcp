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
                verification.Changed ??= verification.Before!.Elements!.Zip(after, (before, current) =>
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

    /// <summary>Captures what a move, rotate or mirror can change: bounds, location and facing/hand orientation.</summary>
    internal static List<double> Pose(Element element)
    {
        var pose = new List<double>();
        using var bounds = element.get_BoundingBox(null);
        if (bounds is not null)
        {
            pose.AddRange(Millimeters(bounds.Min));
            pose.AddRange(Millimeters(bounds.Max));
        }
        switch (element.Location)
        {
            case LocationPoint point:
                pose.AddRange(Millimeters(point.Point));
                pose.Add(Math.Round(point.Rotation, 4));
                break;
            case LocationCurve { Curve: { IsBound: true } curve }:
                pose.AddRange(Millimeters(curve.GetEndPoint(0)));
                pose.AddRange(Millimeters(curve.GetEndPoint(1)));
                break;
        }
        if (element is FamilyInstance instance)
        {
            foreach (var direction in new[] { instance.FacingOrientation, instance.HandOrientation })
                pose.AddRange(new[] { direction.X, direction.Y, direction.Z }.Select(value => Math.Round(value, 4)));
        }
        return pose;
    }

    internal static bool IsHosted(Element element) => element is FamilyInstance { Host: not null };

    private static Element RequiredElement(Document targetDocument, long id) =>
        ActionCommandExecutor.CreateId(id).ToElement(targetDocument)
        ?? throw new InvalidOperationException($"Verification could not find element {id}.");

    private static ActionFacts ParameterFacts(Document targetDocument, ActionJobContract action)
    {
        var element = RequiredElement(targetDocument, action.ElementId);
        var (parameter, candidate) = ActionMutations.ResolveParameter(element, action.Parameter!, action.ParameterId);
        return new ActionFacts
        {
            Id = RevitValueReader.GetId(element.Id),
            Parameter = parameter.Definition.Name,
            ParameterId = candidate.Id,
            Value = ActionMutations.ParameterValue(parameter),
            StorageType = parameter.StorageType.ToString(),
            Owner = candidate.Owner
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
