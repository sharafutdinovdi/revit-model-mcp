using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Units;

namespace RevitModelMcp.Core.Datums;

public static class DatumMatcher
{
    private const double FeetPerMillimeter = 1 / 304.8;

    public static List<LinkDatumItem> Match(IReadOnlyList<DatumRecord> linkDatums,
        IReadOnlyList<DatumRecord> hostDatums, DatumTransform transform, DatumMatchOptions options)
    {
        if (linkDatums is null) throw new ArgumentNullException(nameof(linkDatums));
        if (hostDatums is null) throw new ArgumentNullException(nameof(hostDatums));
        if (transform is null) throw new ArgumentNullException(nameof(transform));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var includeLevels = options.Kinds.Contains("levels");
        var tiltedLevels = includeLevels && (Math.Abs(transform.BasisZ.X) > 1e-9 ||
            Math.Abs(transform.BasisZ.Y) > 1e-9 || Math.Abs(Math.Abs(transform.BasisZ.Z) - 1) > 1e-9);
        if (tiltedLevels && !options.Kinds.Contains("grids"))
            throw new ArgumentException("Link transform is tilted; levels cannot be compared.");

        var includedKinds = options.Kinds.Select(kind => kind == "levels" ? "level" : "grid").ToHashSet();
        var links = linkDatums.Where(datum => includedKinds.Contains(datum.Kind) && (!tiltedLevels || datum.Kind != "level")).ToList();
        var hosts = hostDatums.Where(datum => includedKinds.Contains(datum.Kind) && (!tiltedLevels || datum.Kind != "level")).ToList();
        var available = hosts.Where(datum => !datum.MultiSegment && HasSupportedCurve(datum)).ToDictionary(datum => datum.Id);
        var namedHosts = new Dictionary<long, DatumRecord>();
        foreach (var link in links.Where(datum => !datum.MultiSegment && HasSupportedCurve(datum)))
        {
            var targetName = options.NameMap.TryGetValue(link.Name, out var mappedName)
                ? mappedName : options.Prefix + link.Name + options.Suffix;
            var namedHost = available.Values.FirstOrDefault(datum => datum.Kind == link.Kind && datum.Name == targetName);
            if (namedHost is null) continue;
            namedHosts[link.Id] = namedHost;
            available.Remove(namedHost.Id);
        }
        var result = new List<LinkDatumItem>();
        foreach (var link in links)
        {
            var name = options.NameMap.TryGetValue(link.Name, out var mapped)
                ? mapped : options.Prefix + link.Name + options.Suffix;
            var item = new LinkDatumItem { Kind = link.Kind, Name = name, LinkName = link.Name, LinkId = link.Id };
            if (link.MultiSegment)
            {
                item.Status = "unsupported";
                item.Reason = "multi-segment grid";
                result.Add(item);
                continue;
            }
            if (!HasSupportedCurve(link))
            {
                item.Status = "unsupported";
                item.Reason = "grid curve is unavailable";
                result.Add(item);
                continue;
            }
            var target = Transform(link, transform, options.LevelOffsetMm * FeetPerMillimeter);
            namedHosts.TryGetValue(link.Id, out var host);
            if (host is not null)
            {
                item.MatchedBy = "name";
            }
            else if (options.ReuseMatching)
            {
                host = available.Values.FirstOrDefault(datum => datum.Kind == link.Kind &&
                    Compare(datum, target, options.ToleranceMm, new LinkDatumItem()).Status == "aligned");
                if (host is not null)
                {
                    item.MatchedBy = "geometry";
                    item.NameDiffers = true;
                }
            }
            if (host is null)
            {
                item.Status = "missing_in_host";
            }
            else
            {
                item.HostId = host.Id;
                Compare(host, target, options.ToleranceMm, item);
                available.Remove(host.Id);
            }
            result.Add(item);
        }
        foreach (var host in hosts.Where(datum => datum.MultiSegment))
            result.Add(new LinkDatumItem
            {
                Kind = host.Kind,
                Name = host.Name,
                HostId = host.Id,
                Status = "unsupported",
                Reason = "multi-segment grid"
            });
        foreach (var host in hosts.Where(datum => !datum.MultiSegment && !HasSupportedCurve(datum)))
            result.Add(new LinkDatumItem
            {
                Kind = host.Kind,
                Name = host.Name,
                HostId = host.Id,
                Status = "unsupported",
                Reason = "grid curve is unavailable"
            });
        foreach (var host in available.Values)
            result.Add(new LinkDatumItem { Kind = host.Kind, Name = host.Name, HostId = host.Id, Status = "host_only" });
        if (tiltedLevels)
        {
            foreach (var link in linkDatums.Where(datum => datum.Kind == "level"))
                result.Add(new LinkDatumItem
                {
                    Kind = "level",
                    Name = options.NameMap.TryGetValue(link.Name, out var mapped) ? mapped : options.Prefix + link.Name + options.Suffix,
                    LinkName = link.Name,
                    LinkId = link.Id,
                    Status = "unsupported",
                    Reason = "Link transform is tilted; levels cannot be compared."
                });
            foreach (var host in hostDatums.Where(datum => datum.Kind == "level"))
                result.Add(new LinkDatumItem
                {
                    Kind = "level",
                    Name = host.Name,
                    HostId = host.Id,
                    Status = "unsupported",
                    Reason = "Link transform is tilted; levels cannot be compared."
                });
        }
        return result;
    }

    private static bool HasSupportedCurve(DatumRecord datum) => datum.Kind != "grid" ||
        datum.Center is not null || datum.Start is not null && datum.End is not null;

    private static DatumRecord Transform(DatumRecord datum, DatumTransform transform, double levelOffset)
    {
        if (datum.Kind == "level")
            return datum with { Elevation = transform.OfPoint(new DatumPoint(0, 0, datum.Elevation)).Z + levelOffset };
        return datum with
        {
            Start = datum.Start is null ? null : transform.OfPoint(datum.Start),
            End = datum.End is null ? null : transform.OfPoint(datum.End),
            Center = datum.Center is null ? null : transform.OfPoint(datum.Center)
        };
    }

    private static LinkDatumItem Compare(DatumRecord host, DatumRecord target, double toleranceMm, LinkDatumItem item)
    {
        var tolerance = toleranceMm * FeetPerMillimeter;
        if (host.Kind == "level")
        {
            var difference = target.Elevation - host.Elevation;
            item.DzMm = RoundMm(difference);
            item.Before = new DatumElevation { ElevationMm = RoundMm(host.Elevation) };
            item.After = new DatumElevation { ElevationMm = RoundMm(target.Elevation) };
            item.Status = Math.Abs(difference) <= tolerance ? "aligned" : "differs";
            return item;
        }
        if ((host.Center is null) != (target.Center is null))
        {
            item.Status = "unsupported";
            item.Reason = "line and arc differ";
            return item;
        }
        if (host.Center is not null && target.Center is not null)
        {
            var radiusDifference = target.Radius - host.Radius;
            item.RadiusDeltaMm = RoundMm(radiusDifference);
            if (Math.Abs(radiusDifference) > tolerance)
            {
                item.Status = "unsupported";
                item.Reason = "radius differs";
                return item;
            }
            var centerOffsetX = target.Center.X - host.Center.X;
            var centerOffsetY = target.Center.Y - host.Center.Y;
            item.OffsetMm = [RoundMm(centerOffsetX), RoundMm(centerOffsetY)];
            item.Status = Math.Sqrt(centerOffsetX * centerOffsetX + centerOffsetY * centerOffsetY) <= tolerance ? "aligned" : "differs";
            return item;
        }
        if (host.Start is null || host.End is null || target.Start is null || target.End is null)
        {
            item.Status = "unsupported";
            item.Reason = "grid curve is unavailable";
            return item;
        }
        var hostAngle = Math.Atan2(host.End.Y - host.Start.Y, host.End.X - host.Start.X);
        var targetAngle = Math.Atan2(target.End.Y - target.Start.Y, target.End.X - target.Start.X);
        var rawAngle = NormalizeAngle((targetAngle - hostAngle) * 180 / Math.PI);
        item.Reversed = Math.Abs(rawAngle) > 90;
        var angle = rawAngle;
        if (angle <= -90) angle += 180;
        if (angle > 90) angle -= 180;
        item.AngleDeg = Math.Round(angle, 3);
        var directionX = Math.Cos(targetAngle);
        var directionY = Math.Sin(targetAngle);
        var perpendicularDistance = (target.Start.X - host.Start.X) * -directionY +
                                    (target.Start.Y - host.Start.Y) * directionX;
        var offsetX = -directionY * perpendicularDistance;
        var offsetY = directionX * perpendicularDistance;
        item.OffsetMm = [RoundMm(offsetX), RoundMm(offsetY)];
        item.EndpointsDeltaMm = [RoundMm((target.Start.X - host.Start.X) * directionX +
                                          (target.Start.Y - host.Start.Y) * directionY),
            RoundMm((target.End.X - host.End.X) * directionX + (target.End.Y - host.End.Y) * directionY)];
        item.Status = Math.Abs(perpendicularDistance) <= tolerance && Math.Abs(angle) <= 0.01
            ? "aligned" : "differs";
        return item;
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle <= -180) angle += 360;
        while (angle > 180) angle -= 360;
        return angle;
    }

    private static double RoundMm(double feet) => Math.Round(UnitConverter.FeetToMillimeters(feet), 1);
}
