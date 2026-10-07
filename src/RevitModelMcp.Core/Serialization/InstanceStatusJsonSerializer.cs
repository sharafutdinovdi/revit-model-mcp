using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Serialization;

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
