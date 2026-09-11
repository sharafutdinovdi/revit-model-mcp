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

    public static CropBoxSnapshot ToCropBox(BoundingBoxXYZ box)
    {
        return new CropBoxSnapshot
        {
            MinXmm = ToMillimeters(box.Min.X),
            MinYmm = ToMillimeters(box.Min.Y),
            MinZmm = ToMillimeters(box.Min.Z),
            MaxXmm = ToMillimeters(box.Max.X),
            MaxYmm = ToMillimeters(box.Max.Y),
            MaxZmm = ToMillimeters(box.Max.Z)
        };
    }

    public static SectionCropBoundingBoxSnapshot ToSectionCropBoundingBox(BoundingBoxXYZ box)
    {
        return new SectionCropBoundingBoxSnapshot
        {
            MinX = ToMillimeters(box.Min.X),
            MinY = ToMillimeters(box.Min.Y),
            MinZ = ToMillimeters(box.Min.Z),
            MaxX = ToMillimeters(box.Max.X),
            MaxY = ToMillimeters(box.Max.Y),
            MaxZ = ToMillimeters(box.Max.Z)
        };
    }

    public static BoundingBoxOnViewSnapshot? GetBoundingBoxOnView(Element element, View view)
    {
        try
        {
            var box = element.get_BoundingBox(view);
            if (box is null)
            {
                return null;
            }

            return new BoundingBoxOnViewSnapshot
            {
                YMin = ToMillimeters(box.Min.Y),
                YMax = ToMillimeters(box.Max.Y),
                XMin = ToMillimeters(box.Min.X),
                XMax = ToMillimeters(box.Max.X)
            };
        }
        catch
        {
            return null;
        }
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

    public static string? GetMark(Element element)
    {
        return GetParameterText(element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK));
    }

    public static string? GetNamedParameter(Element element, string name)
    {
        try
        {
            return GetParameterText(element.LookupParameter(name));
        }
        catch
        {
            return null;
        }
    }

    public static double? GetLengthParameterMm(Element element, BuiltInParameter parameterId)
    {
        try
        {
            var parameter = element.get_Parameter(parameterId);
            return parameter is null || !parameter.HasValue
                ? null
                : ToMillimeters(parameter.AsDouble());
        }
        catch
        {
            return null;
        }
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
