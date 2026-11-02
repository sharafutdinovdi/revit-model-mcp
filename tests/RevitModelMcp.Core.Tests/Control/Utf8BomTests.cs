using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class Utf8BomTests
{
    [Test]
    public async Task SettingsWithBomAreDeserialized()
    {
        var path = Path.GetTempFileName();
        try
        {
            var content = Encoding.UTF8.GetBytes("""{"httpPort":53111}""");
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. content]);
            using var stream = new MemoryStream(Utf8Bom.Strip(File.ReadAllBytes(path)));
            var serializer = new DataContractJsonSerializer(typeof(SampleSettings));
            var settings = (SampleSettings)serializer.ReadObject(stream)!;
            await Assert.That(settings.HttpPort).IsEqualTo(53111);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ContentWithoutBomIsReturnedUnchanged()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"httpPort":53111}""");
        await Assert.That(ReferenceEquals(Utf8Bom.Strip(bytes), bytes)).IsTrue();
    }

    [Test]
    public async Task EmptyArrayIsReturnedUnchanged()
    {
        var bytes = Array.Empty<byte>();
        await Assert.That(ReferenceEquals(Utf8Bom.Strip(bytes), bytes)).IsTrue();
    }

    [DataContract]
    private sealed class SampleSettings
    {
        [DataMember(Name = "httpPort")]
        public int HttpPort { get; set; }
    }
}
