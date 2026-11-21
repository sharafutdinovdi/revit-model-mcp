using System.Globalization;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Query;

public sealed record LinkSelector(long? InstanceId, string? Name, bool All)
{
    public static LinkSelector Parse(string? value)
    {
        if (value is null) return new(null, null, false);
        value = value.Trim();
        if (value.Length == 0) throw new ArgumentException("link must not be empty.", nameof(value));
        if (value == "*") return new(null, null, true);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
        {
            if (instanceId <= 0) throw new ArgumentException("link instance ID must be positive.", nameof(value));
            return new(instanceId, null, false);
        }
        return new(null, value, false);
    }

    public bool IsHost => InstanceId is null && Name is null && !All;
    public bool Matches(long instanceId, string name, string typeName) => All ||
        InstanceId == instanceId || Name is not null &&
        (string.Equals(Name, name, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Name, typeName, StringComparison.OrdinalIgnoreCase));
}

public sealed record LinkReadTransform(double[] OriginMm, double[] BasisX, double[] BasisY, double[] BasisZ)
{
    public double[] Point(double[] point) =>
    [
        Math.Round(OriginMm[0] + BasisX[0] * point[0] + BasisY[0] * point[1] + BasisZ[0] * point[2], 1),
        Math.Round(OriginMm[1] + BasisX[1] * point[0] + BasisY[1] * point[1] + BasisZ[1] * point[2], 1),
        Math.Round(OriginMm[2] + BasisX[2] * point[0] + BasisY[2] * point[1] + BasisZ[2] * point[2], 1)
    ];

    public void Apply(ElementGeometryData row, long instanceId, string name)
    {
        row.LinkInstanceId = instanceId;
        row.LinkName = name;
        if (row is QueryElementItem query)
        {
            query.LinkedElementId = query.Id;
            if (query.Values.TryGetValue("id", out var idValue))
            {
                query.Values.Remove("id");
                query.Values["linkedElementId"] = idValue;
            }
        }
        if (row is ElementDetailsData elementDetails)
        {
            elementDetails.LinkedElementId = elementDetails.Element.Id;
            elementDetails.Element.LinkedElementId = elementDetails.Element.Id;
            if (elementDetails.TypeElement is not null)
                elementDetails.TypeElement.LinkedElementId = elementDetails.TypeElement.Id;
        }
        if (row.RoomCenterMm is not null) row.RoomCenterMm = Point(row.RoomCenterMm);
        if (row.Location is not null)
        {
            var location = row.Location;
            if (location.XMm.HasValue && location.YMm.HasValue && location.ZMm.HasValue)
            {
                var point = Point([location.XMm.Value, location.YMm.Value, location.ZMm.Value]);
                location.XMm = point[0];
                location.YMm = point[1];
                location.ZMm = point[2];
            }
            if (location.StartMm is not null) location.StartMm = Point(location.StartMm);
            if (location.EndMm is not null) location.EndMm = Point(location.EndMm);
        }
        if (row.BoundingBox is not null)
        {
            var bounds = row.BoundingBox;
            var minimum = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var maximum = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            for (var corner = 0; corner < 8; corner++)
            {
                var point = Point([
                    (corner & 1) == 0 ? bounds.MinMm[0] : bounds.MaxMm[0],
                    (corner & 2) == 0 ? bounds.MinMm[1] : bounds.MaxMm[1],
                    (corner & 4) == 0 ? bounds.MinMm[2] : bounds.MaxMm[2]]);
                for (var axis = 0; axis < 3; axis++)
                {
                    minimum[axis] = Math.Min(minimum[axis], point[axis]);
                    maximum[axis] = Math.Max(maximum[axis], point[axis]);
                }
            }
            bounds.MinMm = minimum;
            bounds.MaxMm = maximum;
            bounds.CenterMm = Point(bounds.CenterMm);
        }
        if (row is ElementDetailsData { Room: not null } details)
        {
            foreach (var boundary in details.Room.Boundaries.SelectMany(loop => loop.Segments))
            {
                boundary.LinkedElementId = boundary.ElementId;
                var start = Point([boundary.StartMm.X, boundary.StartMm.Y, boundary.StartMm.Z]);
                var end = Point([boundary.EndMm.X, boundary.EndMm.Y, boundary.EndMm.Z]);
                boundary.StartMm = new VectorSnapshot { X = start[0], Y = start[1], Z = start[2] };
                boundary.EndMm = new VectorSnapshot { X = end[0], Y = end[1], Z = end[2] };
            }
        }
    }
}
