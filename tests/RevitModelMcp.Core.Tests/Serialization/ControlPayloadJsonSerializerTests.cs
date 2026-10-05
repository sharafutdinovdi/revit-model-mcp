using System.Text.Json;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Serialization;

public sealed class ControlPayloadJsonSerializerTests
{
    [Test]
    public async Task Serialize_HealthPayload_WritesPlainJsonObjectWithCommandArray()
    {
        var payload = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["revitVersion"] = "2026",
            ["addinVersion"] = "0.9.0",
            ["protocolVersion"] = 3,
            ["commands"] = new[] { "ping", "a", "b" },
            ["documentName"] = "Sample Model.rvt",
            ["processId"] = 4242,
            ["startedUtc"] = "2026-10-05T10:00:00Z",
            ["readOnly"] = false,
            ["uptimeMs"] = 9_000_000_000L
        };

        var json = ControlPayloadJsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        await Assert.That(json).Contains("\"commands\":[\"ping\",\"a\",\"b\"]");
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("revitVersion").GetString()).IsEqualTo("2026");
        await Assert.That(root.GetProperty("addinVersion").GetString()).IsEqualTo("0.9.0");
        await Assert.That(root.GetProperty("protocolVersion").GetInt32()).IsEqualTo(3);
        await Assert.That(root.GetProperty("commands").GetArrayLength()).IsEqualTo(3);
        await Assert.That(root.GetProperty("commands")[0].GetString()).IsEqualTo("ping");
        await Assert.That(root.GetProperty("commands")[1].GetString()).IsEqualTo("a");
        await Assert.That(root.GetProperty("commands")[2].GetString()).IsEqualTo("b");
        await Assert.That(root.GetProperty("documentName").GetString()).IsEqualTo("Sample Model.rvt");
        await Assert.That(root.GetProperty("processId").GetInt32()).IsEqualTo(4242);
        await Assert.That(root.GetProperty("startedUtc").GetString()).IsEqualTo("2026-10-05T10:00:00Z");
        await Assert.That(root.GetProperty("readOnly").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("uptimeMs").GetInt64()).IsEqualTo(9_000_000_000L);
    }

    [Test]
    public async Task Serialize_ExportViewPayload_PreservesScalars()
    {
        var payload = new Dictionary<string, object>
        {
            ["command"] = "export-view",
            ["viewId"] = 123,
            ["includeLinks"] = true
        };

        using var document = JsonDocument.Parse(ControlPayloadJsonSerializer.Serialize(payload));
        var root = document.RootElement;

        await Assert.That(root.GetProperty("command").GetString()).IsEqualTo("export-view");
        await Assert.That(root.GetProperty("viewId").GetInt32()).IsEqualTo(123);
        await Assert.That(root.GetProperty("includeLinks").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Serialize_NullPayload_Throws()
    {
        await Assert.That(() => ControlPayloadJsonSerializer.Serialize(null!)).Throws<ArgumentNullException>();
    }
}
