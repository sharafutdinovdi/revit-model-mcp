using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Serialization;

public static class SnapshotJsonSerializer
{
    private static readonly Type[] KnownTypes =
    {
        typeof(CurtainPanelSnapshot),
        typeof(PanelStatsSnapshot),
        typeof(SectionViewSnapshot),
        typeof(SectionCropBoundingBoxSnapshot),
        typeof(DuplicateBaseNameSnapshot)
    };

    public static string Serialize(Snapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var serializer = new DataContractJsonSerializer(
            typeof(Snapshot),
            new DataContractJsonSerializerSettings
            {
                KnownTypes = KnownTypes,
                UseSimpleDictionaryFormat = true
            });

        using var stream = new MemoryStream();
        serializer.WriteObject(stream, snapshot);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public static class InstanceStatusJsonSerializer
{
    public static string Serialize(InstanceStatus status)
    {
        if (status is null)
        {
            throw new ArgumentNullException(nameof(status));
        }

        var serializer = new DataContractJsonSerializer(typeof(InstanceStatus));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, status);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
