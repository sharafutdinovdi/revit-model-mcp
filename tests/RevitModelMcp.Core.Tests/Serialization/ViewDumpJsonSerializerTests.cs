using System.Text.Json;
using RevitModelMcp.Core.Serialization;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Tests.Formatting;

namespace RevitModelMcp.Core.Tests.Serialization;

public sealed class ViewDumpJsonSerializerTests
{
    [Test]
    public async Task Serialize_PreparedReport_PreservesMachineReadableShape()
    {
        var report = ViewDumpTextFormatterTests.CreatePreparedReport();
        report.Responder = new ResponderInfo { DocumentName = "Sample Model.rvt", ProcessId = 4242 };

        var json = ViewDumpJsonSerializer.Serialize(report);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var view = root.GetProperty("views")[0];
        var element = view.GetProperty("elements")[0];

        await Assert.That(root.GetProperty("command").GetString()).IsEqualTo("views-dump");
        await Assert.That(root.GetProperty("status").GetString()).IsEqualTo("completed");
        await Assert.That(root.GetProperty("responder").GetProperty("documentName").GetString()).IsEqualTo("Sample Model.rvt");
        await Assert.That(view.GetProperty("header").GetProperty("elementCount").GetInt32()).IsEqualTo(1);
        await Assert.That(element.GetProperty("id").GetInt64()).IsEqualTo(11327511);
        await Assert.That(element.GetProperty("areaM2").GetDouble()).IsEqualTo(48.2);
        await Assert.That(element.GetProperty("profileParameters").GetProperty("Project_Area").GetString())
            .IsEqualTo("48.200");
        await Assert.That(element.TryGetProperty("lengthMm", out _)).IsFalse();
        await Assert.That(element.TryGetProperty("thicknessMm", out _)).IsFalse();
        await Assert.That(element.TryGetProperty("volumeM3", out _)).IsFalse();
    }
}
