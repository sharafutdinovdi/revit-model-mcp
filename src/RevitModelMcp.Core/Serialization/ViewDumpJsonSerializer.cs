using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Serialization;

public static class ViewDumpJsonSerializer
{
    public static string Serialize(ViewDumpReport report)
    {
        if (report is null)
        {
            throw new ArgumentNullException(nameof(report));
        }

        var serializer = new DataContractJsonSerializer(
            typeof(ViewDumpReport),
            new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, report);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
