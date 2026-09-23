using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models
{

    public sealed record DatumPoint(double X, double Y, double Z = 0);

    public sealed record DatumRecord(long Id, string Name, string Kind, double Elevation = 0,
        DatumPoint? Start = null, DatumPoint? End = null, DatumPoint? Center = null,
        double Radius = 0, bool MultiSegment = false);

    public sealed record DatumTransform(DatumPoint Origin, DatumPoint BasisX, DatumPoint BasisY, DatumPoint BasisZ)
    {
        public static DatumTransform Identity { get; } = new(new(0, 0), new(1, 0), new(0, 1), new(0, 0, 1));

        public DatumPoint OfPoint(DatumPoint point) => new(
            Origin.X + point.X * BasisX.X + point.Y * BasisY.X + point.Z * BasisZ.X,
            Origin.Y + point.X * BasisX.Y + point.Y * BasisY.Y + point.Z * BasisZ.Y,
            Origin.Z + point.X * BasisX.Z + point.Y * BasisY.Z + point.Z * BasisZ.Z);
    }

    public sealed record DatumMatchOptions(IReadOnlyList<string> Kinds, IReadOnlyDictionary<string, string> NameMap,
        string Prefix = "", string Suffix = "", double LevelOffsetMm = 0, bool ReuseMatching = true,
        double ToleranceMm = 0.5);

    [DataContract]
    public sealed class LinkDatumData
    {
        [DataMember(Name = "link")] public LinkDatumLink Link { get; set; } = new();
        [DataMember(Name = "toleranceMm")] public double ToleranceMm { get; set; }
        [DataMember(Name = "levelOffsetMm")] public double LevelOffsetMm { get; set; }
        [DataMember(Name = "items")] public List<LinkDatumItem> Items { get; set; } = [];
        [DataMember(Name = "summary")] public LinkDatumSummary Summary { get; set; } = new();
        [DataMember(Name = "dryRun", EmitDefaultValue = false)] public bool? DryRun { get; set; }
        [DataMember(Name = "warning", EmitDefaultValue = false)] public string? Warning { get; set; }

        public void UpdateSummary() => Summary = new LinkDatumSummary
        {
            Aligned = Items.Count(item => item.Status == "aligned"),
            Moved = Items.Count(item => item.Status == "moved"),
            Created = Items.Count(item => item.Status == "created"),
            HostOnly = Items.Count(item => item.Status == "host_only"),
            Unsupported = Items.Count(item => item.Status == "unsupported"),
            Skipped = Items.Count(item => item.Status == "skipped")
        };
    }

    [DataContract]
    public sealed class LinkDatumLink
    {
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "loaded")] public bool Loaded { get; set; } = true;
    }

    [DataContract]
    public sealed class LinkDatumSummary
    {
        [DataMember(Name = "aligned")] public int Aligned { get; set; }
        [DataMember(Name = "moved")] public int Moved { get; set; }
        [DataMember(Name = "created")] public int Created { get; set; }
        [DataMember(Name = "hostOnly")] public int HostOnly { get; set; }
        [DataMember(Name = "unsupported")] public int Unsupported { get; set; }
        [DataMember(Name = "skipped")] public int Skipped { get; set; }
    }

    [DataContract]
    public sealed class LinkDatumItem
    {
        [DataMember(Name = "kind")] public string Kind { get; set; } = "";
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "linkName", EmitDefaultValue = false)] public string? LinkName { get; set; }
        [DataMember(Name = "hostId", EmitDefaultValue = false)] public long? HostId { get; set; }
        [DataMember(Name = "linkId", EmitDefaultValue = false)] public long? LinkId { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; } = "";
        [DataMember(Name = "reason", EmitDefaultValue = false)] public string? Reason { get; set; }
        [DataMember(Name = "matchedBy", EmitDefaultValue = false)] public string? MatchedBy { get; set; }
        [DataMember(Name = "nameDiffers", EmitDefaultValue = false)] public bool? NameDiffers { get; set; }
        [DataMember(Name = "dzMm", EmitDefaultValue = false)] public double? DzMm { get; set; }
        [DataMember(Name = "angleDeg", EmitDefaultValue = false)] public double? AngleDeg { get; set; }
        [DataMember(Name = "offsetMm", EmitDefaultValue = false)] public List<double>? OffsetMm { get; set; }
        [DataMember(Name = "endpointsDeltaMm", EmitDefaultValue = false)] public List<double>? EndpointsDeltaMm { get; set; }
        [DataMember(Name = "reversed", EmitDefaultValue = false)] public bool? Reversed { get; set; }
        [DataMember(Name = "radiusDeltaMm", EmitDefaultValue = false)] public double? RadiusDeltaMm { get; set; }
        [DataMember(Name = "before", EmitDefaultValue = false)] public DatumElevation? Before { get; set; }
        [DataMember(Name = "after", EmitDefaultValue = false)] public DatumElevation? After { get; set; }
        [DataMember(Name = "dependentCount", EmitDefaultValue = false)] public int? DependentCount { get; set; }
        [DataMember(Name = "scopeBox")] public string? ScopeBox { get; set; }
        [DataMember(Name = "workset", EmitDefaultValue = false)] public string? Workset { get; set; }
    }

    [DataContract]
    public sealed class DatumElevation
    {
        [DataMember(Name = "elevationMm")] public double ElevationMm { get; set; }
    }
}

#if NET48
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
