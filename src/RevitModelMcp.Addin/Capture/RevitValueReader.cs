using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Units;

namespace RevitModelMcp.Capture;

internal static class RevitValueReader
{
    public static long GetId(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }

    public static bool IsValidId(ElementId? id)
    {
        return id is not null && id != ElementId.InvalidElementId;
    }

    public static double ToMillimeters(double feet)
    {
        return UnitConverter.FeetToMillimeters(feet);
    }

    public static VectorSnapshot ToVector(XYZ point, bool convertToMillimeters)
    {
        return new VectorSnapshot
        {
            X = convertToMillimeters ? ToMillimeters(point.X) : Round(point.X),
            Y = convertToMillimeters ? ToMillimeters(point.Y) : Round(point.Y),
            Z = convertToMillimeters ? ToMillimeters(point.Z) : Round(point.Z)
        };
    }

    public static string? GetFamilyName(Element element)
    {
        var familyName = GetParameterText(element.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME));
        if (!string.IsNullOrWhiteSpace(familyName))
        {
            return familyName;
        }

        return (element as FamilyInstance)?.Symbol?.Family?.Name;
    }

    public static string? GetTypeName(Document document, Element element)
    {
        var typeName = GetParameterText(element.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME));
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            return typeName;
        }

        return document.GetElement(element.GetTypeId())?.Name;
    }

    public static string? GetParameterText(Parameter? parameter)
    {
        if (parameter is null || !parameter.HasValue)
        {
            return null;
        }

        var value = parameter.StorageType == StorageType.String
            ? parameter.AsString()
            : parameter.AsValueString();

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static double Round(double value)
    {
        return Math.Round(value, 9);
    }
}
