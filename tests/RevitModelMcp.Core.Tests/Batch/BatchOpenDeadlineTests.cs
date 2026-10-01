using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchOpenDeadlineTests
{
    [Test]
    public async Task Resolve_UsesRouteDefaults()
    {
        await Assert.That(BatchOpenDeadline.Resolve(null, false)).IsEqualTo(TimeSpan.FromMinutes(30));
        await Assert.That(BatchOpenDeadline.Resolve(null, true)).IsEqualTo(TimeSpan.FromMinutes(45));
    }

    [Test]
    public async Task Resolve_UsesConfiguredValueForBothRoutesAndAcceptsBoundaries()
    {
        foreach (var minutes in new[] { 5, 30, 180 })
        {
            await Assert.That(BatchOpenDeadline.Resolve(minutes, false)).IsEqualTo(TimeSpan.FromMinutes(minutes));
            await Assert.That(BatchOpenDeadline.Resolve(minutes, true)).IsEqualTo(TimeSpan.FromMinutes(minutes));
        }
    }

    [Test]
    public async Task Resolve_RejectsInvalidPersistedValue()
    {
        var serializer = new DataContractJsonSerializer(typeof(BatchRun));
        foreach (var minutes in new[] { 4, 181 })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
                $$"""{"runId":"run","models":[],"openTimeoutMinutes":{{minutes}}}"""));
            var run = (BatchRun)serializer.ReadObject(stream)!;
            await Assert.That(() => BatchOpenDeadline.Resolve(run.OpenTimeoutMinutes, false))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task BatchRun_JsonRoundTripsWithAndWithoutOpenTimeout()
    {
        var serializer = new DataContractJsonSerializer(typeof(BatchRun),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        foreach (var minutes in new int?[] { null, 90 })
        {
            var run = new BatchRun { RunId = "run", Models = [], OpenTimeoutMinutes = minutes };
            using var stream = new MemoryStream();
            serializer.WriteObject(stream, run);
            var json = Encoding.UTF8.GetString(stream.ToArray());
            await Assert.That(json.Contains("openTimeoutMinutes")).IsEqualTo(minutes is not null);
            stream.Position = 0;
            var restored = (BatchRun)serializer.ReadObject(stream)!;
            await Assert.That(restored.OpenTimeoutMinutes).IsEqualTo(minutes);
        }

        using var olderStream = new MemoryStream(Encoding.UTF8.GetBytes("""{"runId":"run","models":[]}"""));
        var olderRun = (BatchRun)serializer.ReadObject(olderStream)!;
        await Assert.That(olderRun.OpenTimeoutMinutes).IsNull();
        await Assert.That(BatchOpenDeadline.Resolve(olderRun.OpenTimeoutMinutes, false))
            .IsEqualTo(TimeSpan.FromMinutes(30));
    }
}
