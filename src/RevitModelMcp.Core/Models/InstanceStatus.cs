using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed class InstanceStatus
{
    [DataMember(Name = "processId", Order = 1)]
    public int ProcessId { get; set; }

    [DataMember(Name = "revitVersion", Order = 2)]
    public string RevitVersion { get; set; } = string.Empty;

    [DataMember(Name = "documentTitle", Order = 3)]
    public string DocumentTitle { get; set; } = string.Empty;

    [DataMember(Name = "documentPath", Order = 4)]
    public string DocumentPath { get; set; } = string.Empty;

    [DataMember(Name = "updatedUtc", Order = 5)]
    public string UpdatedUtc { get; set; } = string.Empty;

    [DataMember(Name = "fileChannelVersion", Order = 6)]
    public int FileChannelVersion { get; set; } = 2;

    [DataMember(Name = "startedUtc", Order = 7)]
    public string StartedUtc { get; set; } = string.Empty;

    [DataMember(Name = "httpPort", Order = 8)]
    public int? HttpPort { get; set; }

    [DataMember(Name = "discoveryVersion", Order = 9)]
    public int DiscoveryVersion { get; set; } = 3;

    [DataMember(Name = "instanceId", Order = 10)]
    public string InstanceId { get; set; } = string.Empty;

    [DataMember(Name = "pipeName", Order = 11, EmitDefaultValue = false)]
    public string? PipeName { get; set; }

    [DataMember(Name = "protocols", Order = 12)]
    public List<string> Protocols { get; set; } = new();

    [DataMember(Name = "documents", Order = 13)]
    public List<InstanceDocument> Documents { get; set; } = new();

    [DataMember(Name = "addinVersion", Order = 14)]
    public string AddinVersion { get; set; } = string.Empty;

    [DataMember(Name = "protocolVersion", Order = 15)]
    public int ProtocolVersion { get; set; } = 1;

    [DataMember(Name = "commands", Order = 16)]
    public List<string> Commands { get; set; } = new();

    [DataMember(Name = "httpState", Order = 17, EmitDefaultValue = false)]
    public string? HttpState { get; set; }

    [DataMember(Name = "httpReason", Order = 18, EmitDefaultValue = false)]
    public string? HttpReason { get; set; }
}

[DataContract]
public sealed class InstanceDocument
{
    [DataMember(Name = "title", Order = 1)]
    public string Title { get; set; } = string.Empty;

    [DataMember(Name = "path", Order = 2)]
    public string Path { get; set; } = string.Empty;

    [DataMember(Name = "isActive", Order = 3)]
    public bool IsActive { get; set; }

    [DataMember(Name = "isFamilyDocument", Order = 4)]
    public bool IsFamilyDocument { get; set; }
}

[DataContract]
public sealed class VectorSnapshot
{
    [DataMember(Name = "x")]
    public double X { get; set; }

    [DataMember(Name = "y")]
    public double Y { get; set; }

    [DataMember(Name = "z")]
    public double Z { get; set; }
}
