using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Nice3point.Revit.Extensions;
using Nice3point.Revit.Toolkit.Options;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Control;

internal static class ActionMutations
{
    private static readonly Dictionary<Document, Dictionary<(long View, long Element), OverrideGraphicSettings>> GraphicHistory = new();

    internal static ActionResultData OverrideGraphics(Document document, ActionJobContract action, List<ElementId> ids)
    {
        using var viewCollector = new FilteredElementCollector(document).OfClass(typeof(View));
        var eligible = viewCollector.Cast<View>().Where(view => !view.IsTemplate &&
            view is ViewPlan or ViewSection or View3D).ToList();
        var views = action.ViewScope switch
        {
            "active" => [document.ActiveView],
            "all" => eligible,
            _ => action.Views!.Select(reference => FindView(document, reference)).Distinct().ToList()
        };
        if (views.Any(view => !eligible.Any(candidate => candidate.Id == view.Id)))
            throw new ArgumentException("Graphics overrides require a non-template plan, section, elevation or 3D view.");
        using var patternCollector = new FilteredElementCollector(document).OfClass(typeof(FillPatternElement));
        var solid = patternCollector.Cast<FillPatternElement>().FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill);
        if (!action.Reset && action.Fill && solid is null)
            throw new InvalidOperationException("The document has no solid fill pattern.");
        var color = new Color(Convert.ToByte(action.Color.Substring(1, 2), 16),
            Convert.ToByte(action.Color.Substring(3, 2), 16), Convert.ToByte(action.Color.Substring(5, 2), 16));
        if (!GraphicHistory.TryGetValue(document, out var history))
            GraphicHistory[document] = history = new Dictionary<(long, long), OverrideGraphicSettings>();
        var saved = new Dictionary<(long, long), OverrideGraphicSettings>();
        var cleared = new List<(long, long)>();
        var perView = new Dictionary<string, int>();
        var touched = new List<string>();
        foreach (var view in views)
        {
            using var visibleCollector = new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType();
            var visible = visibleCollector.ToElementIds().ToHashSet();
            var targets = ids.Where(id => action.Reset || visible.Contains(id)).ToList();
            if (targets.Count == 0) continue;
            var viewId = RevitValueReader.GetId(view.Id);
            foreach (var id in targets)
            {
                var key = (viewId, RevitValueReader.GetId(id));
                if (action.Reset)
                {
                    view.SetElementOverrides(id, history.TryGetValue(key, out var original)
                        ? original : new OverrideGraphicSettings());
                    cleared.Add(key);
                    continue;
                }
                var current = view.GetElementOverrides(id);
                if (!history.ContainsKey(key) && !saved.ContainsKey(key))
                    saved[key] = new OverrideGraphicSettings(current);
                var settings = new OverrideGraphicSettings(current);
                settings.SetProjectionLineColor(color);
                settings.SetCutLineColor(color);
                if (action.Fill)
                {
                    settings.SetSurfaceForegroundPatternId(solid!.Id);
                    settings.SetSurfaceForegroundPatternColor(color);
                    settings.SetCutForegroundPatternId(solid.Id);
                    settings.SetCutForegroundPatternColor(color);
                }
                if (action.LineWeight is int weight)
                {
                    settings.SetProjectionLineWeight(weight);
                    settings.SetCutLineWeight(weight);
                }
                settings.SetSurfaceTransparency(action.Transparency);
                view.SetElementOverrides(id, settings);
            }
            if (action.Reset)
            {
                foreach (var entry in history.Where(entry => entry.Key.View == viewId && entry.Key.Element < 0).ToList())
                {
                    var id = ActionCommandExecutor.CreateId(-entry.Key.Element);
                    if (document.GetElement(id) is not null) view.SetElementOverrides(id, entry.Value);
                    cleared.Add(entry.Key);
                }
            }
            else if (action.HalftoneOthers)
            {
                foreach (var id in visible)
                {
                    if (targets.Contains(id) || document.GetElement(id)?.Category?.CategoryType != CategoryType.Model) continue;
                    var key = (viewId, -RevitValueReader.GetId(id));
                    var current = view.GetElementOverrides(id);
                    if (!history.ContainsKey(key) && !saved.ContainsKey(key)) saved[key] = new OverrideGraphicSettings(current);
                    var settings = new OverrideGraphicSettings(current);
                    settings.SetHalftone(true);
                    view.SetElementOverrides(id, settings);
                }
            }
            touched.Add(view.Name);
            perView[view.Name] = targets.Count;
        }
        if (!action.DryRun)
        {
            foreach (var entry in saved) history[entry.Key] = entry.Value;
            foreach (var key in cleared) history.Remove(key);
        }
        return new ActionResultData { Count = perView.Values.Sum(), ViewsTouched = touched, ElementsPerView = perView };
    }

    internal static ActionResultData Rotate(Document document, ActionJobContract action, List<ElementId> ids)
    {
        var pinned = ids.Where(id => document.GetElement(id)?.Pinned == true).Select(RevitValueReader.GetId).ToList();
        if (pinned.Count > 0) throw new InvalidOperationException($"Pinned elements cannot rotate: {string.Join(", ", pinned)}.");
        var boxes = ids.Select(id => document.GetElement(id)!.get_BoundingBox(null)).ToList();
        if (action.CenterMm is null && boxes.Any(box => box is null))
            throw new InvalidOperationException("All elements need bounding boxes when centerMm is omitted.");
        var x = action.CenterMm is null ? (boxes.Min(box => box!.Min.X) + boxes.Max(box => box!.Max.X)) / 2 : Millimeters(action.CenterMm[0]);
        var y = action.CenterMm is null ? (boxes.Min(box => box!.Min.Y) + boxes.Max(box => box!.Max.Y)) / 2 : Millimeters(action.CenterMm[1]);
        var origin = new XYZ(x, y, 0);
        try
        {
            ElementTransformUtils.RotateElements(document, ids, Line.CreateBound(origin, origin + XYZ.BasisZ), action.AngleDeg * Math.PI / 180);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException exception)
        {
            throw new InvalidOperationException($"Elements cannot rotate: {string.Join(", ", action.ElementIds)}. {exception.Message}", exception);
        }
        return new ActionResultData { Count = ids.Count, Verification = new ActionVerification { Changed = action.ElementIds } };
    }

    internal static ActionResultData Copy(Document document, ActionJobContract action, List<ElementId> ids)
    {
        var copies = new List<List<long>>();
        for (var index = 1; index <= action.Count; index++)
            copies.Add(ElementTransformUtils.CopyElements(document, ids,
                new XYZ(Millimeters(action.DxMm * index), Millimeters(action.DyMm * index), Millimeters(action.DzMm * index)))
                .Select(RevitValueReader.GetId).ToList());
        return new ActionResultData
        {
            Count = copies.Sum(copy => copy.Count),
            Copies = copies,
            Verification = new ActionVerification { Changed = copies.SelectMany(copy => copy).ToList() }
        };
    }

    internal static ActionResultData Mirror(Document document, ActionJobContract action, List<ElementId> ids)
    {
        var incompatible = ids.Where(id => !ElementTransformUtils.CanMirrorElement(document, id))
            .Select(RevitValueReader.GetId).ToList();
        if (incompatible.Count > 0)
            throw new InvalidOperationException($"Elements cannot mirror: {string.Join(", ", incompatible)}.");
        var point = new XYZ(Millimeters(action.PointMm![0]), Millimeters(action.PointMm[1]), 0);
        var direction = action.Axis == "x" ? XYZ.BasisX : XYZ.BasisY;
        var plane = Plane.CreateByNormalAndOrigin(direction.CrossProduct(XYZ.BasisZ), point);
        var created = ElementTransformUtils.MirrorElements(document, ids, plane, action.Copy)
            .Select(RevitValueReader.GetId).ToList();
        return new ActionResultData
        {
            Count = action.Copy ? created.Count : ids.Count,
            Copies = action.Copy ? [created] : null,
            Verification = new ActionVerification { Changed = action.Copy ? created : action.ElementIds }
        };
    }

    internal static ActionResultData ChangeType(Document document, ActionJobContract action, List<ElementId> ids)
    {
        var targets = new List<(Element Element, ElementId TypeId)>();
        foreach (var id in ids)
        {
            var element = document.GetElement(id)!;
            var valid = element.GetValidTypes().Select(typeId => (Id: typeId, Type: document.GetElement(typeId) as ElementType))
                .Where(item => item.Type is not null).ToList();
            if (valid.Count == 0)
                throw new ArgumentException($"Element {RevitValueReader.GetId(id)} has no compatible types.");
            var matching = valid.Where(item => string.Equals(item.Type!.Name, action.TypeName, StringComparison.OrdinalIgnoreCase) &&
                (action.Family is null || string.Equals(item.Type.FamilyName, action.Family, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matching.Count != 1)
                throw new ArgumentException($"Element {RevitValueReader.GetId(id)} has {matching.Count} compatible targets named '{action.TypeName}'. Candidates: {string.Join(", ", valid.Select(item => $"{item.Type!.FamilyName}: {item.Type.Name}"))}.");
            targets.Add((element, matching[0].Id));
        }
        var changed = new List<long>();
        foreach (var (element, typeId) in targets)
        {
            var replacement = element.ChangeTypeId(typeId);
            changed.Add(RevitValueReader.GetId(replacement == ElementId.InvalidElementId ? element.Id : replacement));
        }
        return new ActionResultData { Count = changed.Count, Verification = new ActionVerification { Changed = changed } };
    }

    internal static ActionResultData UpdateParameters(Document document, ActionJobContract action)
    {
        var context = QueryFilterBuilder.Build(document, action.QueryFilters!, []);
        using var collector = context.CreateCollector();
        var ids = collector.ToElementIds().ToList();
        if (ids.Count > action.MaxElements)
            throw new MatchLimitException(ids.Count, action.MaxElements);
        var skipped = new Dictionary<string, List<long>>
        {
            ["missing"] = [],
            ["readOnly"] = [],
            ["typeParameter"] = []
        };
        var result = new ActionResultData { MatchedCount = ids.Count, Values = [], Skipped = skipped };
        var matchedIds = ids.Select(RevitValueReader.GetId).ToHashSet();
        var affectedTypeIds = new HashSet<long>();
        var changedIds = new List<long>();
        foreach (var id in ids)
        {
            var element = document.GetElement(id)!;
            Parameter parameter;
            var isTypeParameter = false;
            try { (parameter, _) = ResolveParameter(element, action.Parameter!, action.ParameterId, false); }
            catch (ArgumentException exception) when (exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    (parameter, _) = ResolveParameter(element, action.Parameter!, action.ParameterId);
                    isTypeParameter = true;
                }
                catch (ArgumentException typeException) when (typeException.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    skipped["missing"].Add(RevitValueReader.GetId(id));
                    continue;
                }
            }
            if (isTypeParameter && !action.IncludeTypeParameters)
            {
                skipped["typeParameter"].Add(RevitValueReader.GetId(id));
                continue;
            }
            if (parameter.IsReadOnly)
            {
                skipped["readOnly"].Add(RevitValueReader.GetId(id));
                continue;
            }
            var targetId = isTypeParameter ? RevitValueReader.GetId(element.GetTypeId()) : RevitValueReader.GetId(id);
            if (isTypeParameter && !affectedTypeIds.Add(targetId)) continue;
            var oldValue = ParameterValue(parameter);
            var value = ParameterResolution.ValidateValue(action.Parameter!, parameter.StorageType.ToString(), action.Value!);
            var changed = parameter.StorageType switch
            {
                StorageType.String => parameter.Set((string)value),
                StorageType.Integer => parameter.Set((int)value),
                StorageType.Double => parameter.Set(ParameterDouble(parameter, (double)value)),
                _ => throw new ArgumentException("Only String, Integer and Double parameters are supported.")
            };
            if (!changed) throw new InvalidOperationException($"Revit could not set parameter on element {targetId}.");
            changedIds.Add(targetId);
            if (result.Values.Count < 50) result.Values.Add(new ParameterChange { Id = targetId, OldValue = oldValue, NewValue = ParameterValue(parameter) });
        }
        if (affectedTypeIds.Count > 0)
        {
            result.AffectedTypeIds = affectedTypeIds.OrderBy(id => id).ToList();
            using var instances = new FilteredElementCollector(document).WhereElementIsNotElementType();
            result.OutsideFilterCount = instances.Count(element =>
                affectedTypeIds.Contains(RevitValueReader.GetId(element.GetTypeId())) &&
                !matchedIds.Contains(RevitValueReader.GetId(element.Id)));
        }
        result.Verification = new ActionVerification { Changed = changedIds };
        result.Count = result.Verification.Changed.Count;
        return result;
    }

    internal sealed class MatchLimitException(int count, int limit)
        : ArgumentException($"Matched {count} elements, exceeding maxElements={limit}.")
    {
        public int Count { get; } = count;
    }
    internal static ActionResultData PlaceFamily(Document document, ActionJobContract action)
    {
        var instance = PlaceFamilyInstance(document, action);
        return new ActionResultData { Id = RevitValueReader.GetId(instance.Id), Category = instance.Category?.Name, Level = action.Level };
    }

    private static FamilyInstance PlaceFamilyInstance(Document document, ActionJobContract action, double zMm = 0, long? hostId = null,
        Dictionary<string, object>? parameters = null)
    {
        using var symbols = document.CollectElements().OfClass<FamilySymbol>()
            .WhereParameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME).Equals(action.Family!);
        if (!symbols.Any())
        {
            using var familyCollector = document.CollectElements().OfClass<Family>();
            var families = familyCollector.Cast<Family>().ToList();
            var categories = families.GroupBy(loaded => loaded.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => string.Join(", ", group.Select(loaded => loaded.FamilyCategory?.Name ?? "Uncategorized").Distinct()),
                    StringComparer.OrdinalIgnoreCase);
            var suggestions = ActionJobParser.ClosestFamilyNames(action.Family!, categories.Keys)
                .Select(name => $"{name} ({categories[name]})").ToList();
            throw new FamilyNotLoadedException(action.Family!, suggestions);
        }
        if (action.TypeName is not null)
            symbols.WhereParameter(BuiltInParameter.SYMBOL_NAME_PARAM).Equals(action.TypeName);
        var symbol = symbols.FirstOrDefault() as FamilySymbol
                     ?? throw new ArgumentException($"Type '{action.TypeName}' was not found in family '{action.Family}'.");
        var level = FindLevel(document, action.Level!);
        if (!symbol.IsActive)
        {
            symbol.Activate();
            document.Regenerate();
        }
        var point = new XYZ(Millimeters(action.XMm), Millimeters(action.YMm), level.ProjectElevation + Millimeters(zMm));
        if (hostId is not null && symbol.Family.FamilyPlacementType is not (FamilyPlacementType.OneLevelBasedHosted or FamilyPlacementType.WorkPlaneBased))
            throw new ArgumentException($"Family '{action.Family}' is not hosted.");
        var host = hostId is null ? null : CreateId(hostId.Value).ToElement(document)
            ?? throw new ArgumentException($"Host {hostId} was not found.");
        if (host is not null && host is not Wall && host is not Floor && host is not Ceiling)
            throw new ArgumentException($"Host {hostId} must be a wall, floor or ceiling.");
        FamilyInstance instance;
        var rotationPoint = point;
        if (host is null)
            instance = document.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
        else if (symbol.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased)
        {
            var (face, projected) = ClosestHostFace(host, point);
            var normal = face.ComputeNormal(projected.UVPoint);
            var direction = normal.CrossProduct(XYZ.BasisZ);
            if (direction.GetLength() < 0.001) direction = normal.CrossProduct(XYZ.BasisX);
            instance = document.Create.NewFamilyInstance(face, projected.XYZPoint, direction.Normalize(), symbol);
            rotationPoint = projected.XYZPoint;
        }
        else
            instance = document.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural);
        if (action.RotationDeg != 0)
            instance.Rotate(Line.CreateBound(rotationPoint, rotationPoint + XYZ.BasisZ), action.RotationDeg * Math.PI / 180);
        if (parameters is not null)
            foreach (var (name, value) in parameters)
            {
                var (_, candidate) = ResolveParameter(instance, name, null);
                if (candidate.Owner != "instance") throw new ArgumentException($"Parameter '{name}' is not an instance parameter.");
                SetParameter(document, new ActionJobContract { ElementId = RevitValueReader.GetId(instance.Id), Parameter = name, Value = value });
            }
        return instance;
    }

    private static (Face Face, IntersectionResult Projection) ClosestHostFace(Element host, XYZ point)
    {
        var options = new Options { ComputeReferences = true };
        var geometry = host.get_Geometry(options);
        var candidates = new List<(Face Face, IntersectionResult Projection)>();
        void Collect(GeometryElement elements)
        {
            foreach (var geometryObject in elements)
            {
                if (geometryObject is GeometryInstance instance)
                {
                    Collect(instance.GetInstanceGeometry());
                    continue;
                }
                if (geometryObject is not Solid solid) continue;
                foreach (Face face in solid.Faces)
                {
                    var projection = face.Project(point);
                    if (projection is not null && face.Reference is not null) candidates.Add((face, projection));
                }
            }
        }
        if (geometry is not null) Collect(geometry);
        if (candidates.Count == 0)
            throw new ArgumentException($"No usable face was found on host {RevitValueReader.GetId(host.Id)}.");
        return candidates.OrderBy(candidate => candidate.Projection.Distance).First();
    }

    internal static ActionResultData LoadFamilies(Document document, ActionJobContract action)
    {
        var result = new ActionResultData { Loaded = [] };
        foreach (var path in action.Paths!) result.Loaded.Add(LoadFamily(document, path, action.Overwrite, action.OverwriteParameterValues));
        result.Count = result.Loaded.Count(item => item.Status is "loaded" or "reloaded");
        return result;
    }

    private static FamilyLoadResult LoadFamily(Document document, string path, bool overwrite, bool overwriteParameterValues)
    {
        var familyName = Path.GetFileNameWithoutExtension(path);
        using var existing = document.CollectElements().OfClass<Family>();
        var loaded = existing.Cast<Family>().FirstOrDefault(family => string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase));
        var wasLoaded = loaded is not null;
        var status = "skipped";
        if (loaded is null || overwrite)
        {
            var loadSucceeded = document.LoadFamily(path, new FamilyLoadOptions(overwriteParameterValues, FamilySource.Project), out var family);
            status = FamilyLoadResult.StatusFor(familyName, wasLoaded, loadSucceeded);
            if (loadSucceeded) loaded = family;
        }
        return new FamilyLoadResult
        {
            Family = loaded!.Name,
            Status = status,
            Types = loaded.GetFamilySymbolIds().Select(id => document.GetElement(id)?.Name ?? string.Empty).OrderBy(name => name).ToList()
        };
    }

    internal static ActionResultData PlaceFamilies(Document document, ActionJobContract action)
    {
        var failed = new List<PlacementFailure>();
        var skipped = new List<PlacementFailure>();
        var result = new ActionResultData { Loaded = [], Failed = failed, Skipped = skipped, CreatedElementIds = [], PerTypeCounts = [] };
        foreach (var path in action.Load ?? []) result.Loaded.Add(LoadFamily(document, path, false, false));
        var placements = action.Placements ?? [];
        if (action.AtRooms is not null)
        {
            var request = action.AtRooms;
            foreach (var room in new FilteredElementCollector(document)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType()
                         .OfType<Autodesk.Revit.DB.Architecture.Room>())
            {
                if (request.Level is not null && !string.Equals(room.Level?.Name, request.Level, StringComparison.OrdinalIgnoreCase)) continue;
                if (request.Rooms is not null && !request.Rooms.Any(name => string.Equals(name, room.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(name, room.Number, StringComparison.OrdinalIgnoreCase))) continue;
                if (room.Level is null || room.Location is not LocationPoint point || room.Area <= 0)
                {
                    skipped.Add(new PlacementFailure { Index = skipped.Count, Reason = $"Room '{room.Number}' is unplaced or unenclosed." });
                    continue;
                }
                placements.Add(new FamilyPlacementContract
                {
                    Family = request.Family,
                    TypeName = request.TypeName,
                    Level = room.Level.Name,
                    XMm = point.Point.X.ToMillimeters(),
                    YMm = point.Point.Y.ToMillimeters(),
                    ZMm = request.ZMm,
                    RotationDeg = request.RotationDeg,
                    Parameters = request.Parameters
                });
            }
            if (placements.Count > 2000) throw new ArgumentException("atRooms selected more than 2000 rooms.");
        }
        for (var index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            using var subtransaction = new SubTransaction(document);
            subtransaction.Start();
            try
            {
                var instance = PlaceFamilyInstance(document, new ActionJobContract
                {
                    Family = placement.Family,
                    TypeName = placement.TypeName,
                    Level = placement.Level,
                    XMm = placement.XMm!.Value,
                    YMm = placement.YMm!.Value,
                    RotationDeg = placement.RotationDeg
                }, placement.ZMm, placement.HostId, placement.Parameters);
                if (subtransaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Placement was rolled back.");
                result.CreatedElementIds.Add(RevitValueReader.GetId(instance.Id));
                var key = $"{placement.Family}: {placement.TypeName}";
                result.PerTypeCounts[key] = result.PerTypeCounts.TryGetValue(key, out var count) ? count + 1 : 1;
            }
            catch (Exception exception)
            {
                if (subtransaction.GetStatus() == TransactionStatus.Started) subtransaction.RollBack();
                if (action.StopOnError) throw new ArgumentException($"Placement {index}: {exception.Message}", exception);
                failed.Add(new PlacementFailure { Index = index, Reason = exception.Message });
            }
        }
        result.Placed = result.CreatedElementIds.Count;
        result.Count = result.Placed;
        result.Verification = new ActionVerification { Changed = result.CreatedElementIds.ToList() };
        return result;
    }

    internal static ActionResultData CreateWall(Document document, ActionJobContract action)
    {
        var level = FindLevel(document, action.Level!);
        using var types = document.CollectElements().OfClass<WallType>();
        if (action.WallType is not null)
            types.WhereParameter(BuiltInParameter.SYMBOL_NAME_PARAM).Equals(action.WallType);
        var wallType = types.Cast<WallType>().FirstOrDefault(candidate => action.WallType is not null || candidate.Kind == WallKind.Basic)
                       ?? throw new ArgumentException($"Wall type '{action.WallType ?? "basic wall"}' was not found.");
        var start = new XYZ(Millimeters(action.StartMm[0]), Millimeters(action.StartMm[1]), level.ProjectElevation);
        var end = new XYZ(Millimeters(action.EndMm[0]), Millimeters(action.EndMm[1]), level.ProjectElevation);
        var line = Line.CreateBound(start, end);
        var wall = Wall.Create(document, line, wallType.Id, level.Id, Millimeters(action.HeightMm), 0, false, false);
        return new ActionResultData { Id = RevitValueReader.GetId(wall.Id), LengthMm = line.Length.ToMillimeters() };
    }

    internal static ActionResultData CreateMepRun(Document document, ActionJobContract action)
    {
        var level = FindLevel(document, action.Level!);
        var offset = action.OffsetMm ?? (action.Kind is "duct" or "cable_tray" ? 2700 : 2500);
        var points = action.PointsMm!.Select(point => new XYZ(Millimeters(point[0]), Millimeters(point[1]),
            point.Count == 3 ? Millimeters(point[2]) : level.ProjectElevation + Millimeters(offset))).ToList();
        if (points.Zip(points.Skip(1), (start, end) => start.DistanceTo(end) <= Millimeters(2.54)).Any(shortSegment => shortSegment))
            throw new ArgumentException("Consecutive points must be more than 2.54 mm apart.");

        var typeId = action.Kind switch
        {
            "duct" => FindMepType<DuctType>(document, action.TypeName).Id,
            "pipe" => FindMepType<PipeType>(document, action.TypeName).Id,
            "cable_tray" => FindMepType<CableTrayType>(document, action.TypeName).Id,
            _ => FindMepType<ConduitType>(document, action.TypeName).Id
        };
        var systemId = action.Kind switch
        {
            "duct" => FindMepType<MechanicalSystemType>(document, action.SystemType).Id,
            "pipe" => FindMepType<PipingSystemType>(document, action.SystemType).Id,
            _ => ElementId.InvalidElementId
        };
        var segments = new List<MEPCurve>();
        var result = new ActionResultData { SegmentIds = [], FittingIds = [], UnjoinedPairs = [], LengthMm = 0 };
        for (var index = 0; index < points.Count - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            var segment = action.Kind switch
            {
                "duct" => (MEPCurve)Duct.Create(document, systemId, typeId, level.Id, start, end),
                "pipe" => Pipe.Create(document, systemId, typeId, level.Id, start, end),
                "cable_tray" => CableTray.Create(document, typeId, start, end, level.Id),
                _ => Conduit.Create(document, typeId, start, end, level.Id)
            };
            SetMepSize(segment, action);
            segments.Add(segment);
            result.SegmentIds.Add(RevitValueReader.GetId(segment.Id));
            result.LengthMm += start.DistanceTo(end).ToMillimeters();
        }
        document.Regenerate();
        for (var index = 1; index < segments.Count; index++)
        {
            var previous = NearestConnector(segments[index - 1], points[index]);
            var current = NearestConnector(segments[index], points[index]);
            using var fittingTransaction = new SubTransaction(document);
            fittingTransaction.Start();
            try
            {
                var fitting = document.Create.NewElbowFitting(previous, current);
                if (fittingTransaction.Commit() == TransactionStatus.Committed)
                    result.FittingIds.Add(RevitValueReader.GetId(fitting.Id));
                else
                    result.UnjoinedPairs.Add([result.SegmentIds[index - 1], result.SegmentIds[index]]);
            }
            catch (Exception exception) when (exception is Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                if (fittingTransaction.GetStatus() == TransactionStatus.Started) fittingTransaction.RollBack();
                result.UnjoinedPairs.Add([result.SegmentIds[index - 1], result.SegmentIds[index]]);
            }
        }
        if (action.ConnectTo is not null)
        {
            var existing = document.GetElement(CreateId(action.ConnectTo.Value));
            var connectorManager = existing switch
            {
                MEPCurve curve => curve.ConnectorManager,
                FamilyInstance instance => instance.MEPModel?.ConnectorManager,
                _ => null
            } ?? throw new ArgumentException($"Element {action.ConnectTo} has no MEP connectors.");
            var start = NearestConnector(segments[0], points[0]);
            var target = connectorManager.Connectors.Cast<Connector>()
                .Where(connector => !connector.IsConnected && connector.Domain == start.Domain &&
                    connector.Origin.DistanceTo(start.Origin) <= Millimeters(50))
                .OrderBy(connector => connector.Origin.DistanceTo(start.Origin)).FirstOrDefault();
            target?.ConnectTo(start);
        }
        result.Count = result.SegmentIds.Count;
        result.Verification = new ActionVerification { Changed = result.SegmentIds.Concat(result.FittingIds).ToList() };
        return result;
    }

    private static T FindMepType<T>(Document document, string? name) where T : ElementType
    {
        using var types = document.CollectElements().OfClass<T>();
        var match = types.Cast<T>().FirstOrDefault(type => name is null ||
            string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentException($"{typeof(T).Name} '{name ?? "default"}' was not found.");
    }

    private static Connector NearestConnector(MEPCurve segment, XYZ point) =>
        segment.ConnectorManager.Connectors.Cast<Connector>()
            .OrderBy(connector => connector.Origin.DistanceTo(point)).First();

    private static void SetMepSize(MEPCurve segment, ActionJobContract action)
    {
        var shape = segment.ConnectorManager.Connectors.Cast<Connector>().First().Shape;
        if (action.Kind == "duct" && action.DiameterMm is not null && shape != ConnectorProfileType.Round)
            throw new ArgumentException("diameterMm requires a round duct type.");
        if (action.Kind == "duct" && (action.WidthMm is not null || action.MepHeightMm is not null) && shape != ConnectorProfileType.Rectangular)
            throw new ArgumentException("widthMm and heightMm require a rectangular duct type.");
        SetSize(action.WidthMm, action.Kind == "cable_tray" ? BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM : BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
        SetSize(action.MepHeightMm, action.Kind == "cable_tray" ? BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM : BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
        SetSize(action.DiameterMm, action.Kind == "conduit" ? BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM : BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

        void SetSize(double? millimeters, BuiltInParameter parameterId)
        {
            if (millimeters is null) return;
            var parameter = segment.get_Parameter(parameterId);
            if (parameter is null || parameter.IsReadOnly || !parameter.Set(Millimeters(millimeters.Value)))
                throw new ArgumentException($"Cannot set {parameterId} on the selected {action.Kind} type.");
        }
    }

    internal static ActionResultData LinkCad(Document document, ActionJobContract action)
    {
        var view = action.View is not null
            ? ReadCommandReader.FindView(document, action.View) as ViewPlan
            : action.Level is not null
                ? document.CollectElements().OfClass<ViewPlan>().Cast<ViewPlan>()
                    .FirstOrDefault(candidate => !candidate.IsTemplate && candidate.ViewType == ViewType.FloorPlan &&
                        string.Equals(candidate.GenLevel?.Name, action.Level, StringComparison.OrdinalIgnoreCase))
                : document.ActiveView as ViewPlan;
        if (view is null || view.IsTemplate || view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan))
            throw new ArgumentException("A plan view is required. Supply view or a level with a floor plan.");
        using var options = new DWGImportOptions
        {
            ThisViewOnly = true,
            Placement = action.Origin switch
            {
                "shared" => ImportPlacement.Shared,
                "center" => ImportPlacement.Centered,
                _ => ImportPlacement.Origin
            },
            Unit = action.Units switch
            {
                "mm" => ImportUnit.Millimeter,
                "cm" => ImportUnit.Centimeter,
                "m" => ImportUnit.Meter,
                "in" => ImportUnit.Inch,
                "ft" => ImportUnit.Foot,
                _ => ImportUnit.Default
            }
        };
        if (action.Layers is not null) options.SetLayerSelection(action.Layers);
        ImportInstance instance;
        if (action.CadLink)
            instance = ImportInstance.Create(document, view, action.DocumentPath!, options, out _);
        else
        {
            if (!document.Import(action.DocumentPath!, options, view, out var id))
                throw new InvalidOperationException("DWG import failed.");
            instance = document.GetElement(id) as ImportInstance
                ?? throw new InvalidOperationException("DWG import did not create an import instance.");
        }
        document.Regenerate();
        var segments = ReadCadSegments(document, instance);
        var points = segments.SelectMany(segment => new[] { segment.Start, segment.End }).ToList();
        var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (instance.Category?.SubCategories is not null)
            foreach (Category layer in instance.Category.SubCategories)
                layerCounts[layer.Name] = 0;
        foreach (var segment in segments)
            layerCounts[segment.Layer] = layerCounts.GetValueOrDefault(segment.Layer) + 1;
        return new ActionResultData
        {
            Id = RevitValueReader.GetId(instance.Id),
            CadLayers = layerCounts.Select(pair => new CadLayerResult { Name = pair.Key, LineCount = pair.Value })
                .OrderBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ExtentsMm = points.Count == 0 ? null :
            [
                [points.Min(point => point.X).ToMillimeters(), points.Min(point => point.Y).ToMillimeters()],
                [points.Max(point => point.X).ToMillimeters(), points.Max(point => point.Y).ToMillimeters()]
            ],
            Verification = new ActionVerification { Changed = [RevitValueReader.GetId(instance.Id)] }
        };
    }

    internal static ActionResultData WallsFromCad(Document document, ActionJobContract action)
    {
        var instance = document.GetElement(CreateId(action.CadId)) as ImportInstance
            ?? throw new ArgumentException($"CAD import instance {action.CadId} was not found.");
        var level = FindLevel(document, action.Level!);
        var selectedLayers = new HashSet<string>(action.Layers!, StringComparer.OrdinalIgnoreCase);
        var source = ReadCadSegments(document, instance).Where(segment => selectedLayers.Contains(segment.Layer)).ToList();
        var minimumLength = Millimeters(action.MinLengthMm);
        var skippedShort = source.Count(segment => segment.Length < minimumLength);
        var segments = source.Where(segment => segment.Length >= minimumLength).ToList();
        var candidates = new List<(int First, int Second, double Start, double End, double Thickness)>();
        for (var first = 0; first < segments.Count; first++)
        for (var second = first + 1; second < segments.Count; second++)
        {
            var left = segments[first];
            var right = segments[second];
            var direction = (left.End - left.Start).Normalize();
            var otherDirection = (right.End - right.Start).Normalize();
            if (Math.Abs(direction.DotProduct(otherDirection)) < Math.Cos(Math.PI / 180)) continue;
            var offset = right.Start - left.Start;
            var thickness = Math.Abs(direction.X * offset.Y - direction.Y * offset.X);
            if (thickness < Millimeters(action.MinThicknessMm) || thickness > Millimeters(action.MaxThicknessMm)) continue;
            var rightStart = offset.DotProduct(direction);
            var rightEnd = (right.End - left.Start).DotProduct(direction);
            var start = Math.Max(0, Math.Min(rightStart, rightEnd));
            var end = Math.Min(left.Length, Math.Max(rightStart, rightEnd));
            if (end - start < minimumLength) continue;
            candidates.Add((first, second, start, end, thickness));
        }
        var basicTypes = document.CollectElements().OfClass<WallType>().Cast<WallType>()
            .Where(type => type.Kind == WallKind.Basic).ToList();
        if (basicTypes.Count == 0)
            return new ActionResultData
            {
                Count = 0,
                Walls = [],
                UnpairedLines = segments.Count,
                SkippedShortSegments = skippedShort,
                Warning = "No basic wall type exists in this project.",
                Verification = new ActionVerification { Changed = [] }
            };
        var selectedType = action.WallType is null ? null : basicTypes.FirstOrDefault(type =>
            string.Equals(type.Name, action.WallType, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Basic wall type '{action.WallType}' was not found.");
        var paired = new HashSet<int>();
        var planned = new List<(CadWallResult Result, WallType Type, XYZ Start, XYZ End)>();
        foreach (var pair in candidates.OrderByDescending(pair => pair.End - pair.Start))
        {
            if (paired.Contains(pair.First) || paired.Contains(pair.Second)) continue;
            var first = segments[pair.First];
            var second = segments[pair.Second];
            var direction = (first.End - first.Start).Normalize();
            var oppositePoint = first.Start + direction * (second.Start - first.Start).DotProduct(direction);
            var normalOffset = second.Start - oppositePoint;
            var start = first.Start + direction * pair.Start + normalOffset / 2;
            var end = first.Start + direction * pair.End + normalOffset / 2;
            start = new XYZ(start.X, start.Y, level.ProjectElevation);
            end = new XYZ(end.X, end.Y, level.ProjectElevation);
            var type = selectedType ?? basicTypes.OrderBy(candidate => Math.Abs(candidate.Width - pair.Thickness)).First();
            planned.Add((new CadWallResult
            {
                Type = type.Name,
                ThicknessMm = pair.Thickness.ToMillimeters(),
                TypeMismatchMm = Math.Abs(type.Width - pair.Thickness).ToMillimeters(),
                LengthMm = (end - start).GetLength().ToMillimeters(),
                StartMm = [start.X.ToMillimeters(), start.Y.ToMillimeters()],
                EndMm = [end.X.ToMillimeters(), end.Y.ToMillimeters()]
            }, type, start, end));
            paired.Add(pair.First);
            paired.Add(pair.Second);
        }
        if (action.Join)
        {
            var tolerance = Millimeters(10);
            for (var first = 0; first < planned.Count; first++)
            for (var second = first + 1; second < planned.Count; second++)
            for (var firstEnd = 0; firstEnd < 2; firstEnd++)
            for (var secondEnd = 0; secondEnd < 2; secondEnd++)
            {
                var anchor = firstEnd == 0 ? planned[first].Start : planned[first].End;
                var target = secondEnd == 0 ? planned[second].Start : planned[second].End;
                if (anchor.DistanceTo(target) > tolerance) continue;
                var item = planned[second];
                planned[second] = secondEnd == 0
                    ? (item.Result, item.Type, anchor, item.End)
                    : (item.Result, item.Type, item.Start, anchor);
            }
            foreach (var wall in planned)
            {
                wall.Result.StartMm = [wall.Start.X.ToMillimeters(), wall.Start.Y.ToMillimeters()];
                wall.Result.EndMm = [wall.End.X.ToMillimeters(), wall.End.Y.ToMillimeters()];
                wall.Result.LengthMm = (wall.End - wall.Start).GetLength().ToMillimeters();
            }
        }
        var created = new List<Wall>();
        if (!action.DryRun)
        {
            foreach (var wall in planned)
            {
                var element = Wall.Create(document, Line.CreateBound(wall.Start, wall.End), wall.Type.Id,
                    level.Id, Millimeters(action.HeightMm), 0, false, false);
                WallUtils.DisallowWallJoinAtEnd(element, 0);
                WallUtils.DisallowWallJoinAtEnd(element, 1);
                wall.Result.Id = RevitValueReader.GetId(element.Id);
                created.Add(element);
            }
        }
        if (action.Join && !action.DryRun)
        {
            var tolerance = Millimeters(10);
            for (var index = 0; index < planned.Count; index++)
            for (var end = 0; end < 2; end++)
            {
                var point = end == 0 ? planned[index].Start : planned[index].End;
                if (planned.Where((_, other) => other != index).Any(other =>
                    point.DistanceTo(other.Start) <= tolerance || point.DistanceTo(other.End) <= tolerance))
                    WallUtils.AllowWallJoinAtEnd(created[index], end);
            }
        }
        return new ActionResultData
        {
            Count = planned.Count,
            Walls = planned.Select(wall => wall.Result).ToList(),
            UnpairedLines = segments.Count - paired.Count,
            SkippedShortSegments = skippedShort,
            Verification = new ActionVerification { Changed = created.Select(wall => RevitValueReader.GetId(wall.Id)).ToList() }
        };
    }

    private sealed record CadSegment(XYZ Start, XYZ End, string Layer)
    {
        public double Length => (End - Start).GetLength();
    }

    private static List<CadSegment> ReadCadSegments(Document document, ImportInstance instance)
    {
        var segments = new List<CadSegment>();
        var geometry = instance.get_Geometry(new Options { IncludeNonVisibleObjects = false });
        if (geometry is null) return segments;
        void Add(GeometryElement objects)
        {
            foreach (var item in objects)
            {
                if (item is GeometryInstance nested)
                {
                    Add(nested.GetInstanceGeometry());
                    continue;
                }
                var layer = (document.GetElement(item.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name;
                if (string.IsNullOrWhiteSpace(layer)) continue;
                if (item is Line line)
                    segments.Add(new CadSegment(line.GetEndPoint(0), line.GetEndPoint(1), layer!));
                else if (item is PolyLine polyline)
                {
                    var points = polyline.GetCoordinates();
                    for (var index = 1; index < points.Count; index++)
                        if (points[index - 1].DistanceTo(points[index]) > 1e-9)
                            segments.Add(new CadSegment(points[index - 1], points[index], layer!));
                }
            }
        }
        Add(geometry);
        return segments;
    }

    internal static ActionResultData CreateView(Document document, ActionJobContract action)
    {
        var family = action.Kind switch
        {
            "floor_plan" => ViewFamily.FloorPlan,
            "ceiling_plan" => ViewFamily.CeilingPlan,
            "structural_plan" => ViewFamily.StructuralPlan,
            "section" => ViewFamily.Section,
            "3d" => ViewFamily.ThreeDimensional,
            _ => ViewFamily.Drafting
        };
        using var types = document.CollectElements().OfClass<ViewFamilyType>();
        var type = types.Cast<ViewFamilyType>().FirstOrDefault(candidate => candidate.ViewFamily == family &&
            (action.ViewFamilyType is null || string.Equals(candidate.Name, action.ViewFamilyType, StringComparison.OrdinalIgnoreCase)))
            ?? throw new ArgumentException($"View family type '{action.ViewFamilyType ?? family.ToString()}' was not found.");
        View view;
        if (action.Kind is "floor_plan" or "ceiling_plan" or "structural_plan")
            view = ViewPlan.Create(document, type.Id, FindLevel(document, action.Level!).Id);
        else if (action.Kind == "drafting")
            view = ViewDrafting.Create(document, type.Id);
        else if (action.Kind == "3d")
        {
            var created = View3D.CreateIsometric(document, type.Id);
            created.SetSectionBox(ResolveBox(document, action));
            created.IsSectionBoxActive = true;
            view = created;
        }
        else
        {
            var bounds = ResolveBox(document, action);
            var minimum = bounds.Min;
            var maximum = bounds.Max;
            var center = (minimum + maximum) / 2;
            var transform = Transform.Identity;
            transform.Origin = center;
            transform.BasisX = XYZ.BasisX;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = -XYZ.BasisY;
            var (sectionMinimum, sectionMaximum) = SectionBoxBounds.FromExtents(
                maximum.X - minimum.X, maximum.Y - minimum.Y, maximum.Z - minimum.Z);
            var sectionBox = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(sectionMinimum[0], sectionMinimum[1], sectionMinimum[2]),
                Max = new XYZ(sectionMaximum[0], sectionMaximum[1], sectionMaximum[2])
            };
            view = ViewSection.CreateSection(document, type.Id, sectionBox);
        }
        SetViewName(document, view, action.Name);
        if (action.Scale is int scale) view.Scale = scale;
        if (action.Template is not null) ApplyTemplate(document, view, action.Template);
        return new ActionResultData
        {
            Id = RevitValueReader.GetId(view.Id),
            ViewId = RevitValueReader.GetId(view.Id),
            ViewName = view.Name,
            Category = "View",
            Names = [view.Name],
            Count = 1
        };
    }

    internal static ActionResultData DuplicateView(Document document, ActionJobContract action)
    {
        var source = FindView(document, action.View!);
        var option = action.Mode switch
        {
            "with_detailing" => ViewDuplicateOption.WithDetailing,
            "dependent" => ViewDuplicateOption.AsDependent,
            _ => ViewDuplicateOption.Duplicate
        };
        if (!source.CanViewBeDuplicated(option)) throw new ArgumentException($"View '{source.Name}' cannot be duplicated in this mode.");
        var duplicate = document.GetElement(source.Duplicate(option)) as View
            ?? throw new InvalidOperationException("Duplicated view was not found.");
        SetViewName(document, duplicate, action.Name);
        return new ActionResultData
        {
            Id = RevitValueReader.GetId(duplicate.Id),
            ViewId = RevitValueReader.GetId(duplicate.Id),
            ViewName = duplicate.Name,
            Category = "View",
            Names = [duplicate.Name],
            Count = 1
        };
    }

    internal static ActionResultData ApplyViewTemplate(Document document, ActionJobContract action)
    {
        var names = new List<string>();
        var mismatches = new List<string>();
        var changed = new List<long>();
        foreach (var reference in action.Views!)
        {
            var view = FindView(document, reference);
            if (!TemplateMatches(document, view, action.Template!))
            {
                mismatches.Add(view.Name);
                continue;
            }
            ApplyTemplate(document, view, action.Template!);
            names.Add(view.Name);
            changed.Add(RevitValueReader.GetId(view.Id));
        }
        return new ActionResultData
        {
            Count = names.Count,
            Names = names,
            TypeMismatches = mismatches,
            Verification = new ActionVerification { Changed = changed }
        };
    }

    internal static ActionResultData CreateSheet(Document document, ActionJobContract action)
    {
        using var sheets = document.CollectElements().OfClass<ViewSheet>();
        if (sheets.Cast<ViewSheet>().Any(sheet => string.Equals(sheet.SheetNumber, action.Number, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Sheet number '{action.Number}' already exists.");
        using var blocks = document.CollectElements().OfClass<FamilySymbol>().OfCategory(BuiltInCategory.OST_TitleBlocks);
        var titleBlock = blocks.Cast<FamilySymbol>().FirstOrDefault(symbol => action.TitleBlock is null ||
            string.Equals(symbol.FamilyName, action.TitleBlock, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{symbol.FamilyName}: {symbol.Name}", action.TitleBlock, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Title block '{action.TitleBlock ?? "loaded"}' was not found.");
        var sheet = ViewSheet.Create(document, titleBlock.Id);
        sheet.SheetNumber = action.Number!;
        sheet.Name = action.Name!;
        return new ActionResultData
        {
            Id = RevitValueReader.GetId(sheet.Id),
            SheetId = RevitValueReader.GetId(sheet.Id),
            SheetNumber = sheet.SheetNumber,
            SheetName = sheet.Name,
            Category = "Sheet",
            Names = [sheet.SheetNumber],
            Count = 1
        };
    }

    internal static ActionResultData PlaceViewsOnSheet(Document document, ActionJobContract action)
    {
        using var sheets = document.CollectElements().OfClass<ViewSheet>();
        var sheet = sheets.Cast<ViewSheet>().FirstOrDefault(candidate =>
            string.Equals(candidate.SheetNumber, action.Sheet, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, action.Sheet, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Sheet '{action.Sheet}' was not found.");
        var outline = sheet.Outline;
        var gap = Millimeters(20);
        var cursorX = outline.Min.U + gap;
        var cursorY = outline.Max.V - gap;
        var rowHeight = 0.0;
        var names = new List<string>();
        var viewportIds = new List<long>();
        var scheduleInstanceIds = new List<long>();
        foreach (var placement in action.Placements!)
        {
            var view = FindView(document, placement.View);
            if (view is ViewSchedule schedule)
            {
                if (schedule.IsTitleblockRevisionSchedule) throw new ArgumentException("Revision schedules cannot be placed directly.");
                var existingSchedule = new FilteredElementCollector(document).OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>().FirstOrDefault(instance => instance.ScheduleId == schedule.Id && !instance.IsTitleblockRevisionSchedule);
                if (existingSchedule is not null)
                    throw new ArgumentException($"View '{view.Name}' is already on another sheet.");
            }
            else if (!Viewport.CanAddViewToSheet(document, sheet.Id, view.Id))
                throw new ArgumentException($"View '{view.Name}' cannot be placed or is already on another sheet.");
            var width = Math.Max(gap, view.Outline.Max.U - view.Outline.Min.U);
            var height = Math.Max(gap, view.Outline.Max.V - view.Outline.Min.V);
            if (!placement.XMm.HasValue && cursorX + width > outline.Max.U - gap)
            {
                cursorX = outline.Min.U + gap;
                cursorY -= rowHeight + gap;
                rowHeight = 0;
            }
            var x = placement.XMm.HasValue ? Millimeters(placement.XMm.Value) : cursorX + width / 2;
            var y = placement.YMm.HasValue ? Millimeters(placement.YMm.Value) : cursorY - height / 2;
            if (view is ViewSchedule viewSchedule)
                scheduleInstanceIds.Add(RevitValueReader.GetId(ScheduleSheetInstance.Create(document, sheet.Id, viewSchedule.Id, new XYZ(x, y, 0)).Id));
            else
                viewportIds.Add(RevitValueReader.GetId(Viewport.Create(document, sheet.Id, view.Id, new XYZ(x, y, 0)).Id));
            names.Add(view.Name);
            cursorX += width + gap;
            rowHeight = Math.Max(rowHeight, height);
        }
        return new ActionResultData
        {
            Category = "Sheet",
            Names = names,
            Count = names.Count,
            ViewportIds = viewportIds,
            ScheduleInstanceIds = scheduleInstanceIds,
            Verification = new ActionVerification { Changed = [RevitValueReader.GetId(sheet.Id)] }
        };
    }

    private static BoundingBoxXYZ ResolveBox(Document document, ActionJobContract action)
    {
        if (action.Box is not null)
            return new BoundingBoxXYZ
            {
                Min = Point(action.Box.MinMm),
                Max = Point(action.Box.MaxMm)
            };
        var boxes = action.ElementIds.Select(id => ActionCommandExecutor.CreateId(id).ToElement(document)
            ?? throw new ArgumentException($"Element {id} was not found."))
            .Select(element => element.get_BoundingBox(null)
                ?? throw new ArgumentException($"Element {RevitValueReader.GetId(element.Id)} has no bounding box.")).ToList();
        var padding = Millimeters(1000);
        return new BoundingBoxXYZ
        {
            Min = new XYZ(boxes.Min(box => box.Min.X) - padding, boxes.Min(box => box.Min.Y) - padding, boxes.Min(box => box.Min.Z) - padding),
            Max = new XYZ(boxes.Max(box => box.Max.X) + padding, boxes.Max(box => box.Max.Y) + padding, boxes.Max(box => box.Max.Z) + padding)
        };
    }

    private static XYZ Point(IReadOnlyList<double> values) => new(Millimeters(values[0]), Millimeters(values[1]), Millimeters(values[2]));

    private static View FindView(Document document, string reference) =>
        ReadCommandReader.FindView(document, reference) ?? throw new ArgumentException($"View '{reference}' was not found.");

    private static void SetViewName(Document document, View view, string? name)
    {
        if (name is null) return;
        using var views = document.CollectElements().OfClass<View>();
        if (views.Cast<View>().Any(candidate => candidate.Id != view.Id && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"View name '{name}' already exists.");
        view.Name = name;
    }

    private static bool TemplateMatches(Document document, View view, string name)
    {
        using var views = document.CollectElements().OfClass<View>();
        var template = views.Cast<View>().FirstOrDefault(candidate => candidate.IsTemplate &&
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"View template '{name}' was not found.");
        return view.ViewType == template.ViewType;
    }

    private static void ApplyTemplate(Document document, View view, string name)
    {
        using var views = document.CollectElements().OfClass<View>();
        var template = views.Cast<View>().FirstOrDefault(candidate => candidate.IsTemplate &&
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"View template '{name}' was not found.");
        if (view.ViewType != template.ViewType)
            throw new ArgumentException($"View '{view.Name}' does not match template '{name}' type.");
        view.ViewTemplateId = template.Id;
    }

    internal static ActionResultData SetParameter(Document document, ActionJobContract action)
    {
        var element = CreateId(action.ElementId).ToElement(document)
                      ?? throw new ArgumentException($"Element {action.ElementId} was not found.");
        var (parameter, candidate) = ResolveParameter(element, action.Parameter!, action.ParameterId);
        if (parameter.IsReadOnly) throw new InvalidOperationException($"Parameter '{action.Parameter}' is read-only.");
        var oldValue = ParameterValue(parameter);
        var value = ParameterResolution.ValidateValue(action.Parameter!, parameter.StorageType.ToString(), action.Value!);
        var changed = parameter.StorageType switch
        {
            StorageType.String => parameter.Set((string)value),
            StorageType.Integer => parameter.Set((int)value),
            StorageType.Double => parameter.Set(ParameterDouble(parameter, (double)value)),
            _ => throw new ArgumentException("Only String, Integer and Double parameters are supported; ElementId parameters cannot be set.")
        };
        if (!changed)
            throw new InvalidOperationException($"Revit could not set parameter '{action.Parameter}'.");
        return new ActionResultData
        {
            OldValue = oldValue,
            NewValue = ParameterValue(parameter),
            ParameterScope = candidate.Owner
        };
    }

    internal static (Parameter Parameter, ParameterCandidate Candidate) ResolveParameter(Element element, string name, string? parameterId, bool includeType = true)
    {
        var found = new List<(Parameter Parameter, ParameterCandidate Candidate)>();
        void Collect(Element owner, string scope)
        {
            foreach (Parameter parameter in owner.Parameters)
            {
                var id = RevitValueReader.GetId(parameter.Id);
                var builtInName = id is >= int.MinValue and < 0
                    ? Enum.GetName(typeof(BuiltInParameter), (int)id) : null;
                var kind = parameter.IsShared ? "shared" : builtInName is not null ? "built-in" : "project";
                var stableId = parameter.IsShared ? parameter.GUID.ToString("D")
                    : builtInName ?? id.ToString(CultureInfo.InvariantCulture);
                found.Add((parameter, new ParameterCandidate(stableId, parameter.Definition.Name,
                    parameter.StorageType.ToString(), scope, kind, builtInName, id > 0 ? id : null)));
            }
        }
        Collect(element, "instance");
        if (includeType)
        {
            var type = element.Document.GetElement(element.GetTypeId());
            if (type is not null) Collect(type, "type");
        }
        var selected = ParameterResolution.Resolve(name, parameterId, found.Select(item => item.Candidate));
        return found.First(item => item.Candidate == selected);
    }

    private static double ParameterDouble(Parameter parameter, double value)
    {
        var spec = parameter.Definition.GetDataType();
        if (spec == SpecTypeId.Length) return Millimeters(value);
        if (spec == SpecTypeId.Area) return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.SquareMeters);
        return value;
    }

    internal static string ParameterValue(Parameter parameter)
    {
        if (!parameter.HasValue) return string.Empty;
        if (parameter.StorageType == StorageType.String) return parameter.AsString() ?? string.Empty;
        if (parameter.StorageType == StorageType.Integer) return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
        if (parameter.StorageType != StorageType.Double) throw new ArgumentException("Unsupported parameter storage type.");
        var spec = parameter.Definition.GetDataType();
        var value = parameter.AsDouble();
        if (spec == SpecTypeId.Length) value = value.ToMillimeters();
        else if (spec == SpecTypeId.Area) value = UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.SquareMeters);
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static Level FindLevel(Document document, string name)
    {
        using var levels = document.CollectElements().OfClass<Level>()
            .WhereParameter(BuiltInParameter.DATUM_TEXT).Equals(name);
        return levels.FirstOrDefault() as Level
               ?? throw new ArgumentException($"Level '{name}' was not found.");
    }

    private static ElementId CreateId(long value) => ActionCommandExecutor.CreateId(value);
    private static double Millimeters(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

    internal sealed class FamilyNotLoadedException(string name, List<string> closestFamilies)
        : ArgumentException($"Family '{name}' is not loaded. Closest loaded families: {string.Join(", ", closestFamilies)}")
    {
        public List<string> ClosestFamilies { get; } = closestFamilies;
    }
}
