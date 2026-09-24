using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Naming;

namespace RevitModelMcp.Control;

internal static class ActionMutations
{
    internal static ActionResultData PlaceFamily(Document document, ActionJobContract action)
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
        var point = new XYZ(Millimeters(action.XMm), Millimeters(action.YMm), level.ProjectElevation);
        var instance = document.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
        if (action.RotationDeg != 0)
            instance.Rotate(Line.CreateBound(point, point + XYZ.BasisZ), action.RotationDeg * Math.PI / 180);
        return new ActionResultData { Id = RevitValueReader.GetId(instance.Id), Category = instance.Category?.Name, Level = level.Name };
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
        var parameter = ResolveParameter(element, action.Parameter!)
                        ?? throw new ArgumentException($"Parameter '{action.Parameter}' was not found on the instance or type.");
        if (parameter.IsReadOnly) throw new InvalidOperationException($"Parameter '{action.Parameter}' is read-only.");
        var oldValue = ParameterValue(parameter);
        var value = action.Value!;
        var changed = parameter.StorageType switch
        {
            StorageType.String => parameter.Set(value),
            StorageType.Integer => parameter.Set(int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            StorageType.Double => parameter.Set(ParameterDouble(parameter, value)),
            _ => throw new ArgumentException("Only String, Integer and Double parameters are supported; ElementId parameters cannot be set.")
        };
        if (!changed)
            throw new InvalidOperationException($"Revit could not set parameter '{action.Parameter}'.");
        return new ActionResultData
        {
            OldValue = oldValue,
            NewValue = ParameterValue(parameter),
            ParameterScope = parameter.Element.Id == element.Id ? "instance" : "type"
        };
    }

    /// <summary>
    /// Finds a parameter on the instance or its type by localized name, then by <c>BuiltInParameter</c> name or
    /// the English label of a common built-in, so requests work in any Revit UI language.
    /// </summary>
    internal static Parameter? ResolveParameter(Element element, string name)
    {
        if (element.FindParameter(name) is { } byName) return byName;
        var type = element.Document.GetElement(element.GetTypeId());
        foreach (var candidate in ParameterNames.BuiltInCandidates(name))
        {
            if (!Enum.TryParse(candidate, out BuiltInParameter builtIn) || !Enum.IsDefined(typeof(BuiltInParameter), builtIn)) continue;
            if ((element.get_Parameter(builtIn) ?? type?.get_Parameter(builtIn)) is { } parameter) return parameter;
        }
        return null;
    }

    private static double ParameterDouble(Parameter parameter, string text)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Parameter value must be finite.");
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
