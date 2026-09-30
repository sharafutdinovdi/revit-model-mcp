using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Units;

namespace RevitModelMcp.Capture;

internal sealed class ViewElementReader
{
    private static readonly string[] ProfilePrefixes = { "Project_" };
    private static readonly string[] LengthNames = { "Length" };
    private static readonly string[] ThicknessNames = { "Thickness" };
    private static readonly string[] AreaNames = { "Area" };
    private static readonly string[] VolumeNames = { "Volume" };

    private readonly Document _document;
    private readonly HashSet<long> _warningElementIds;

    public ViewElementReader(Document document, HashSet<long> warningElementIds)
    {
        _document = document;
        _warningElementIds = warningElementIds;
    }

    public ViewElementDump Read(Element element)
    {
        var result = new ViewElementDump
        {
            Id = RevitValueReader.GetId(element.Id),
            Category = TryRead($"element {RevitValueReader.GetId(element.Id)} category", () => element.Category?.Name),
            Family = TryRead($"element {RevitValueReader.GetId(element.Id)} family", () => RevitValueReader.GetFamilyName(element)),
            Type = TryRead($"element {RevitValueReader.GetId(element.Id)} type", () => RevitValueReader.GetTypeName(_document, element)),
            Name = TryRead($"element {RevitValueReader.GetId(element.Id)} name", () => element.Name),
            Level = TryRead($"element {RevitValueReader.GetId(element.Id)} level", () => GetLevelName(element)),
            Workset = TryRead($"element {RevitValueReader.GetId(element.Id)} workset", () => GetWorksetName(element)),
            Phase = TryRead($"element {RevitValueReader.GetId(element.Id)} phase", () => GetPhaseName(element)),
            HasWarnings = _warningElementIds.Contains(RevitValueReader.GetId(element.Id))
        };

        ReadParameters(element, result, true);
        var type = TryRead($"element {RevitValueReader.GetId(element.Id)} type", () => _document.GetElement(element.GetTypeId()));
        if (type is not null)
        {
            ReadParameters(type, result, false);
        }

        return result;
    }

    public ElementDetailsData ReadDetails(Element element)
    {
        var result = new ElementDetailsData
        {
            Element = Read(element),
            Parameters = ReadAllParameters(element),
            Warnings = ReadWarnings(element)
        };

        ReadGeometry(element, result);

        if (element is Room room)
        {
            result.Room = ReadRoom(room);
        }

        var type = TryRead($"element {RevitValueReader.GetId(element.Id)} type", () => _document.GetElement(element.GetTypeId()));
        if (type is null)
        {
            return result;
        }

        result.TypeElement = new ElementTypeDetails
        {
            Id = RevitValueReader.GetId(type.Id),
            Family = TryRead($"type {RevitValueReader.GetId(type.Id)} family", () => RevitValueReader.GetFamilyName(type)),
            Name = TryRead($"type {RevitValueReader.GetId(type.Id)} name", () => type.Name),
            Parameters = ReadAllParameters(type)
        };
        return result;
    }

    public static void ReadGeometry(Element element, ElementGeometryData result)
    {
        try
        {
            ReadGeometryCore(element, result);
        }
        catch (Exception exception)
        {
            PluginLog.Skipped($"element {RevitValueReader.GetId(element.Id)} geometry", exception);
        }
    }

    private static void ReadGeometryCore(Element element, ElementGeometryData result)
    {
        var location = element.Location;
        if (location is LocationPoint point)
        {
            var coordinates = CoordinatesMm(point.Point);
            result.Location = new ElementLocationData
            {
                Type = "point",
                XMm = coordinates[0],
                YMm = coordinates[1],
                ZMm = coordinates[2]
            };
            if (element is Room room && room.Area > 0)
                result.RoomCenterMm = coordinates;
        }
        else if (location is LocationCurve curveLocation && curveLocation.Curve is { IsBound: true } curve)
        {
            result.Location = new ElementLocationData
            {
                Type = "curve",
                StartMm = CoordinatesMm(curve.GetEndPoint(0)),
                EndMm = CoordinatesMm(curve.GetEndPoint(1)),
                LengthMm = Math.Round(curve.Length.ToMillimeters(), 1)
            };
        }

        using var bounds = element.get_BoundingBox(null);
        if (bounds is null) return;
        result.BoundingBox = new ElementBoundingBoxData
        {
            MinMm = CoordinatesMm(bounds.Min),
            MaxMm = CoordinatesMm(bounds.Max),
            CenterMm = CoordinatesMm((bounds.Min + bounds.Max) / 2)
        };
    }

    private static double[] CoordinatesMm(XYZ point) =>
        [Math.Round(point.X.ToMillimeters(), 1), Math.Round(point.Y.ToMillimeters(), 1), Math.Round(point.Z.ToMillimeters(), 1)];

    private List<ElementWarningInfo> ReadWarnings(Element element)
    {
        return _document.GetWarnings()
            .Where(warning => warning.GetFailingElements().Contains(element.Id) ||
                              warning.GetAdditionalElements().Contains(element.Id))
            .Select(warning => new ElementWarningInfo
            {
                Text = warning.GetDescriptionText(),
                Severity = warning.GetSeverity().ToString()
            })
            .ToList();
    }

    private static RoomDetails ReadRoom(Room room)
    {
        var boundaries = room.GetBoundarySegments(new SpatialElementBoundaryOptions()) ??
                         new List<IList<BoundarySegment>>();
        return new RoomDetails
        {
            Level = room.Level?.Name,
            AreaM2 = UnitConverter.SquareFeetToSquareMeters(room.Area),
            VolumeM3 = UnitConverter.CubicFeetToCubicMeters(room.Volume),
            Boundaries = boundaries.Select(loop => new RoomBoundaryLoop
            {
                Segments = loop.Select(segment => new RoomBoundarySegment
                {
                    ElementId = RevitValueReader.GetId(segment.ElementId),
                    LengthMm = UnitConverter.FeetToMillimeters(segment.GetCurve().Length),
                    StartMm = RevitValueReader.ToVector(segment.GetCurve().GetEndPoint(0), true),
                    EndMm = RevitValueReader.ToVector(segment.GetCurve().GetEndPoint(1), true)
                }).ToList()
            }).ToList()
        };
    }

    public string? GetTypeIdentity(Element element, ViewElementDump dump)
    {
        var typeId = TryRead($"element {RevitValueReader.GetId(element.Id)} type ID", () => element.GetTypeId());
        if (RevitValueReader.IsValidId(typeId))
        {
            return RevitValueReader.GetId(typeId!).ToString(CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(dump.Family) && string.IsNullOrWhiteSpace(dump.Type)
            ? null
            : $"{dump.Family ?? string.Empty}\u001f{dump.Type ?? string.Empty}";
    }

    private void ReadParameters(Element source, ViewElementDump target, bool overwriteMeasurements)
    {
        foreach (Parameter parameter in source.Parameters)
        {
            try
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrWhiteSpace(name) || !parameter.HasValue)
                {
                    continue;
                }

                var parameterName = name!;
                if (ProfilePrefixes.Any(prefix => parameterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) &&
                    !target.ProfileParameters.ContainsKey(parameterName))
                {
                    var value = ReadProfileValue(parameter);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        target.ProfileParameters[parameterName] = value!;
                    }
                }

                ReadMeasurement(parameter, parameterName, target, overwriteMeasurements);
            }
            catch (Exception exception)
            {
                PluginLog.Skipped($"element {RevitValueReader.GetId(source.Id)} parameter {RevitValueReader.GetId(parameter.Id)}", exception);
            }
        }
    }

    private static void ReadMeasurement(
        Parameter parameter,
        string name,
        ViewElementDump target,
        bool overwrite)
    {
        if (parameter.StorageType != StorageType.Double)
        {
            return;
        }

        var value = parameter.AsDouble();
        if (Matches(name, LengthNames) && (overwrite || !target.LengthMm.HasValue))
        {
            target.LengthMm = UnitConverter.FeetToMillimeters(value);
        }
        else if (Matches(name, ThicknessNames) && (overwrite || !target.ThicknessMm.HasValue))
        {
            target.ThicknessMm = UnitConverter.FeetToMillimeters(value);
        }
        else if (Matches(name, AreaNames) && (overwrite || !target.AreaM2.HasValue))
        {
            target.AreaM2 = UnitConverter.SquareFeetToSquareMeters(value);
        }
        else if (Matches(name, VolumeNames) && (overwrite || !target.VolumeM3.HasValue))
        {
            target.VolumeM3 = UnitConverter.CubicFeetToCubicMeters(value);
        }
    }

    private string? GetLevelName(Element element)
    {
        var levelId = element.LevelId;
        if (RevitValueReader.IsValidId(levelId))
        {
            return _document.GetElement(levelId)?.Name;
        }

        return RevitValueReader.GetParameterText(element.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)) ??
               RevitValueReader.GetParameterText(element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM));
    }

    private string? GetWorksetName(Element element)
    {
        if (!_document.IsWorkshared)
        {
            return null;
        }

        return _document.GetWorksetTable().GetWorkset(element.WorksetId)?.Name;
    }

    private string? GetPhaseName(Element element)
    {
        var parameter = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
        var phaseId = parameter?.AsElementId();
        return RevitValueReader.IsValidId(phaseId) ? _document.GetElement(phaseId)?.Name : null;
    }

    private static string? ReadProfileValue(Parameter parameter)
    {
        var displayValue = parameter.AsValueString();
        if (!string.IsNullOrWhiteSpace(displayValue))
        {
            return displayValue;
        }

        if (parameter.StorageType == StorageType.Double)
        {
            var value = parameter.AsDouble();
            var dataType = parameter.Definition.GetDataType();
            if (dataType == SpecTypeId.Length)
            {
                return $"{UnitConverter.FeetToMillimeters(value).ToString("0.###", CultureInfo.InvariantCulture)} mm";
            }

            if (dataType == SpecTypeId.Area)
            {
                return $"{UnitConverter.SquareFeetToSquareMeters(value).ToString("0.######", CultureInfo.InvariantCulture)} m²";
            }

            if (dataType == SpecTypeId.Volume)
            {
                return $"{UnitConverter.CubicFeetToCubicMeters(value).ToString("0.######", CultureInfo.InvariantCulture)} m³";
            }

            return value.ToString("0.#########", CultureInfo.InvariantCulture);
        }

        return parameter.StorageType switch
        {
            StorageType.String => parameter.AsString(),
            StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.ElementId => RevitValueReader.GetId(parameter.AsElementId()).ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    internal static List<ElementParameterDetail> ReadAllParameters(Element element)
    {
        var parameters = new List<ElementParameterDetail>();
        foreach (Parameter parameter in element.Parameters)
        {
            try
            {
                parameters.Add(ReadParameterDetail(parameter));
            }
            catch (Exception exception)
            {
                PluginLog.Skipped($"element {RevitValueReader.GetId(element.Id)} parameter {RevitValueReader.GetId(parameter.Id)}", exception);
            }
        }

        return parameters
            .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
            .ThenBy(parameter => parameter.StorageType, StringComparer.Ordinal)
            .ToList();
    }

    internal static ElementParameterDetail ReadParameterDetail(Parameter parameter)
    {
        var detail = new ElementParameterDetail
        {
            Name = parameter.Definition?.Name ?? "<unnamed>",
            StorageType = parameter.StorageType.ToString(),
            HasValue = parameter.HasValue
        };
        if (!parameter.HasValue)
        {
            return detail;
        }

        detail.Value = ReadProfileValue(parameter);
        detail.InternalValue = parameter.StorageType switch
        {
            StorageType.Double => parameter.AsDouble().ToString("0.################", CultureInfo.InvariantCulture),
            StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.String => parameter.AsString(),
            StorageType.ElementId => RevitValueReader.GetId(parameter.AsElementId()).ToString(CultureInfo.InvariantCulture),
            _ => null
        };

        if (parameter.StorageType != StorageType.Double)
        {
            return detail;
        }

        var value = parameter.AsDouble();
        var dataType = parameter.Definition?.GetDataType();
        if (dataType == SpecTypeId.Length)
        {
            detail.MetricValue = UnitConverter.FeetToMillimeters(value);
            detail.MetricUnit = "mm";
        }
        else if (dataType == SpecTypeId.Area)
        {
            detail.MetricValue = UnitConverter.SquareFeetToSquareMeters(value);
            detail.MetricUnit = "m2";
        }
        else if (dataType == SpecTypeId.Volume)
        {
            detail.MetricValue = UnitConverter.CubicFeetToCubicMeters(value);
            detail.MetricUnit = "m3";
        }

        return detail;
    }

    private static bool Matches(string name, IEnumerable<string> candidates)
    {
        return candidates.Any(candidate => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static T? TryRead<T>(string what, Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            PluginLog.Skipped(what, exception);
            return default;
        }
    }
}
