using System.Runtime.Serialization.Json;
using System.Text;

namespace RevitModelMcp.Core.Serialization;

public static class ControlPayloadJsonSerializer
{
    private static readonly DataContractJsonSerializer Serializer = new(
        typeof(Dictionary<string, object>),
        new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true,
            KnownTypes = new[]
            {
                typeof(string[]),
                typeof(int[]),
                typeof(long[]),
                typeof(object[]),
                typeof(Dictionary<string, object>),
                typeof(Dictionary<string, string>)
            }
        });

    public static string Serialize(Dictionary<string, object> value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        using var stream = new MemoryStream();
        Serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
