using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Serialization;

public static class InstanceStatusJsonSerializer
{
    private static readonly DataContractJsonSerializer Serializer = new(typeof(InstanceStatus));

    public static string Serialize(InstanceStatus status)
    {
        if (status is null)
        {
            throw new ArgumentNullException(nameof(status));
        }

        using var stream = new MemoryStream();
        Serializer.WriteObject(stream, status);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
