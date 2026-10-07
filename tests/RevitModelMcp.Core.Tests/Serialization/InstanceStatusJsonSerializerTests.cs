using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Serialization;

public sealed class InstanceStatusJsonSerializerTests
{
    [Test]
    [Arguments(null)]
    [Arguments(53110)]
    public async Task Serialize_InstanceStatus_PreservesChannelContract(int? httpPort)
    {
        var status = new InstanceStatus
        {
            ProcessId = 4242,
            RevitVersion = "2023",
            DocumentTitle = "SampleModel",
            DocumentPath = @"C:\\Models\\SampleModel.rvt",
            UpdatedUtc = "2026-08-17T09:15:30.0000000Z",
            StartedUtc = "2026-08-17T09:00:00.0000000Z",
            HttpPort = httpPort,
            InstanceId = "0f8fad5bd9cb469fa16570867728950e",
            PipeName = "RevitModelMcp.4242",
            Protocols = ["pipe/1", "file/2"],
            AddinVersion = "0.6.0",
            ProtocolVersion = 1,
            Commands = ["ping", "document-info"],
            Documents =
            [
                new InstanceDocument { Title = "SampleModel", Path = @"C:\Models\SampleModel.rvt", IsActive = true },
                new InstanceDocument { Title = "Door", IsFamilyDocument = true }
            ]
        };

        using var json = JsonDocument.Parse(InstanceStatusJsonSerializer.Serialize(status));
        var root = json.RootElement;

        await Assert.That(root.GetProperty("fileChannelVersion").GetInt32()).IsEqualTo(2);
        await Assert.That(root.GetProperty("startedUtc").GetString()).IsEqualTo(status.StartedUtc);
        await Assert.That(root.GetProperty("httpPort").ValueKind).IsEqualTo(httpPort.HasValue ? JsonValueKind.Number : JsonValueKind.Null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.GetRawText()));
        var restored = (InstanceStatus)new DataContractJsonSerializer(typeof(InstanceStatus)).ReadObject(stream)!;
        await Assert.That(restored.FileChannelVersion).IsEqualTo(2);
        await Assert.That(restored.StartedUtc).IsEqualTo(status.StartedUtc);
        await Assert.That(restored.HttpPort).IsEqualTo(httpPort);
        await Assert.That(root.GetProperty("processId").GetInt32()).IsEqualTo(4242);
        await Assert.That(root.GetProperty("revitVersion").GetString()).IsEqualTo("2023");
        await Assert.That(root.GetProperty("documentTitle").GetString()).IsEqualTo("SampleModel");
        await Assert.That(root.GetProperty("documentPath").GetString()).IsEqualTo(@"C:\\Models\\SampleModel.rvt");
        await Assert.That(root.GetProperty("updatedUtc").GetString()).IsEqualTo("2026-08-17T09:15:30.0000000Z");
        await Assert.That(root.GetProperty("discoveryVersion").GetInt32()).IsEqualTo(3);
        await Assert.That(root.GetProperty("instanceId").GetString()).IsEqualTo(status.InstanceId);
        await Assert.That(root.GetProperty("pipeName").GetString()).IsEqualTo("RevitModelMcp.4242");
        await Assert.That(root.GetProperty("protocols")[0].GetString()).IsEqualTo("pipe/1");
        await Assert.That(root.GetProperty("addinVersion").GetString()).IsEqualTo("0.6.0");
        await Assert.That(root.GetProperty("protocolVersion").GetInt32()).IsEqualTo(1);
        await Assert.That(root.GetProperty("commands")[1].GetString()).IsEqualTo("document-info");
        var documents = root.GetProperty("documents");
        await Assert.That(documents.GetArrayLength()).IsEqualTo(2);
        await Assert.That(documents[0].GetProperty("path").GetString()).IsEqualTo(@"C:\Models\SampleModel.rvt");
        await Assert.That(documents[0].GetProperty("isActive").GetBoolean()).IsTrue();
        await Assert.That(documents[1].GetProperty("isFamilyDocument").GetBoolean()).IsTrue();
    }

    [Test]
    [Arguments("failed", "Access denied.")]
    [Arguments("listening", null)]
    [Arguments(null, null)]
    public async Task Serialize_InstanceStatus_HttpStatusRoundTripsAndOmitsNulls(string? state, string? reason)
    {
        var status = new InstanceStatus { HttpState = state, HttpReason = reason };
        var serialized = InstanceStatusJsonSerializer.Serialize(status);
        using var json = JsonDocument.Parse(serialized);
        await Assert.That(json.RootElement.TryGetProperty("httpState", out _)).IsEqualTo(state is not null);
        await Assert.That(json.RootElement.TryGetProperty("httpReason", out _)).IsEqualTo(reason is not null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(serialized));
        var restored = (InstanceStatus)new DataContractJsonSerializer(typeof(InstanceStatus)).ReadObject(stream)!;
        await Assert.That(restored.HttpState).IsEqualTo(state);
        await Assert.That(restored.HttpReason).IsEqualTo(reason);
    }

}
