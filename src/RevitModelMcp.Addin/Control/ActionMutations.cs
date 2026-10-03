using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.Revit.Extensions;
using Nice3point.Revit.Toolkit.Options;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class ActionMutations
{
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
        var result = new ActionResultData { Loaded = [], Failed = [], Skipped = [], CreatedElementIds = [], PerTypeCounts = [] };
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
                    result.Skipped.Add(new PlacementFailure { Index = result.Skipped.Count, Reason = $"Room '{room.Number}' is unplaced or unenclosed." });
                    continue;
                }
                placements.Add(new FamilyPlacementContract
                {
                    Family = request.Family, TypeName = request.TypeName, Level = room.Level.Name,
                    XMm = point.Point.X.ToMillimeters(), YMm = point.Point.Y.ToMillimeters(),
                    ZMm = request.ZMm, RotationDeg = request.RotationDeg, Parameters = request.Parameters
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
                    Family = placement.Family, TypeName = placement.TypeName, Level = placement.Level,
                    XMm = placement.XMm, YMm = placement.YMm, RotationDeg = placement.RotationDeg
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
                result.Failed.Add(new PlacementFailure { Index = index, Reason = exception.Message });
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

    internal static (Parameter Parameter, ParameterCandidate Candidate) ResolveParameter(Element element, string name, string? parameterId)
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
        var type = element.Document.GetElement(element.GetTypeId());
        if (type is not null) Collect(type, "type");
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
