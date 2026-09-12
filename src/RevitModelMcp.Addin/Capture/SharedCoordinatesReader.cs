using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class SharedCoordinatesReader
{
    public static SharedCoordinatesData Read(Document document, ControlJobContract job)
    {
        const int listLimit = 100;
        var location = document.ActiveProjectLocation;
        var basePoint = BasePoint.GetProjectBasePoint(document);
        var surveyPoint = BasePoint.GetSurveyPoint(document);
        var locations = document.ProjectLocations.Cast<ProjectLocation>().Select(site => site.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToList();
        using var links = new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance));
        return new SharedCoordinatesData
        {
            ActiveProjectLocation = location.Name,
            SiteName = location.Name,
            ProjectLocations = locations.Take(listLimit).ToList(),
            ProjectLocationsTotal = locations.Count,
            ListLimit = listLimit,
            LinkInstancesTotal = links.GetElementCount(),
            ProjectBasePoint = ReadPoint(basePoint, true),
            SurveyPoint = ReadPoint(surveyPoint, false),
            InternalOriginToBasePointMm = Offset(basePoint.Position),
            TrueNorthAngleDeg = Degrees(location.GetProjectPosition(XYZ.Zero).Angle),
            SharedSiteFromLinks = links.Cast<RevitLinkInstance>().OrderBy(link => RevitValueReader.GetId(link.Id))
                .Take(listLimit).Select(link =>
                {
                    var transform = link.GetTotalTransform();
                    return new LinkSharedSite
                    {
                        LinkName = link.Name,
                        HasOffset = !transform.AlmostEqual(Transform.Identity),
                        OffsetMm = Offset(transform.Origin),
                        RotationDeg = Degrees(Math.Atan2(transform.BasisX.Y, transform.BasisX.X))
                    };
                }).ToList()
        };
    }

    private static CoordinatePoint ReadPoint(BasePoint point, bool project)
    {
        return new CoordinatePoint
        {
            EastWestMm = Millimeters(point.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM).AsDouble()),
            NorthSouthMm = Millimeters(point.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM).AsDouble()),
            ElevationMm = Millimeters(point.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM).AsDouble()),
            AngleToTrueNorthDeg = project
                ? Degrees(point.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM).AsDouble()) : null,
            Clipped = project ? ReadClipped(point) : null
        };
    }

    private static bool? ReadClipped(BasePoint point)
    {
        try { return point.Clipped; }
        catch (Exception) { return null; }
    }

    private static CoordinateOffset Offset(XYZ point) => new()
    {
        X = Millimeters(point.X), Y = Millimeters(point.Y), Z = Millimeters(point.Z)
    };

    private static double Millimeters(double value) =>
        Math.Round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters), 1);

    private static double Degrees(double value) => Math.Round(value * 180 / Math.PI, 1);
}
