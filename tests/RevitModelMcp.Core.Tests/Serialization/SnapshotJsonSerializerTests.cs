using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Serialization;

public sealed class SnapshotJsonSerializerTests
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
        var documents = root.GetProperty("documents");
        await Assert.That(documents.GetArrayLength()).IsEqualTo(2);
        await Assert.That(documents[0].GetProperty("path").GetString()).IsEqualTo(@"C:\Models\SampleModel.rvt");
        await Assert.That(documents[0].GetProperty("isActive").GetBoolean()).IsTrue();
        await Assert.That(documents[1].GetProperty("isFamilyDocument").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Serialize_SectionInventory_UsesExactJsonShape()
    {
        var snapshot = new Snapshot
        {
            Responder = new ResponderInfo { DocumentName = "Sample Model.rvt", ProcessId = 4242 },
            AllSectionViews =
            {
                new SectionViewSnapshot
                {
                    Id = 42,
                    Name = "Coordination Section (1)",
                    TemplateName = "Coordination Template",
                    Scale = 50,
                    CropActive = true,
                    CropBboxMm = new SectionCropBoundingBoxSnapshot
                    {
                        MinX = 1,
                        MinY = 2,
                        MinZ = 3,
                        MaxX = 4,
                        MaxY = 5,
                        MaxZ = 6
                    },
                    IsNameDuplicate = true,
                    Prefix = "Coordination"
                }
            },
            DuplicateBaseNames =
            {
                new DuplicateBaseNameSnapshot { BaseName = "Coordination Section", Count = 2 }
            }
        };

        var json = SnapshotJsonSerializer.Serialize(snapshot);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var section = root.GetProperty("allSectionViews")[0];
        var crop = section.GetProperty("cropBboxMm");
        var duplicate = root.GetProperty("duplicateBaseNames")[0];

        await Assert.That(section.GetProperty("id").GetInt64()).IsEqualTo(42);
        await Assert.That(section.GetProperty("name").GetString()).IsEqualTo("Coordination Section (1)");
        await Assert.That(section.GetProperty("templateName").GetString()).IsEqualTo("Coordination Template");
        await Assert.That(section.GetProperty("scale").GetInt32()).IsEqualTo(50);
        await Assert.That(section.GetProperty("cropActive").GetBoolean()).IsTrue();
        await Assert.That(section.GetProperty("isNameDuplicate").GetBoolean()).IsTrue();
        await Assert.That(section.GetProperty("prefix").GetString()).IsEqualTo("Coordination");
        await Assert.That(crop.GetProperty("minX").GetDouble()).IsEqualTo(1);
        await Assert.That(crop.GetProperty("maxZ").GetDouble()).IsEqualTo(6);
        await Assert.That(duplicate.GetProperty("baseName").GetString()).IsEqualTo("Coordination Section");
        await Assert.That(duplicate.GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(root.GetProperty("responder").GetProperty("processId").GetInt32()).IsEqualTo(4242);
    }

    [Test]
    public async Task Serialize_AnnotationAndSelection_UseTheirExactShapes()
    {
        var snapshot = new Snapshot
        {
            Annotations =
            {
                new AnnotationSnapshot
                {
                    Id = 10,
                    Category = "Detail Items",
                    Params = { ["Mark"] = "A" }
                }
            },
            Selection =
            {
                new SelectionSnapshot
                {
                    Id = 20,
                    Category = "Generic Models",
                    Mark = "B",
                    BboxOnViewMm = new BoundingBoxOnViewSnapshot()
                }
            }
        };

        var json = SnapshotJsonSerializer.Serialize(snapshot);
        using var document = JsonDocument.Parse(json);
        var annotation = document.RootElement.GetProperty("annotations")[0];
        var selection = document.RootElement.GetProperty("selection")[0];

        await Assert.That(annotation.TryGetProperty("params", out _)).IsTrue();
        await Assert.That(annotation.TryGetProperty("mark", out _)).IsFalse();
        await Assert.That(annotation.TryGetProperty("bboxOnViewMm", out _)).IsFalse();
        await Assert.That(selection.TryGetProperty("mark", out _)).IsTrue();
        await Assert.That(selection.TryGetProperty("bboxOnViewMm", out _)).IsTrue();
        await Assert.That(selection.TryGetProperty("params", out _)).IsFalse();
    }

    [Test]
    public async Task Serialize_CurtainPanelsAndStats_UseTheirExactShapes()
    {
        var snapshot = new Snapshot
        {
            CurtainPanelsCapped = true,
            CurtainPanels =
            {
                new CurtainPanelSnapshot
                {
                    Id = 30,
                    FamilyName = "Panel Family",
                    TypeName = "Panel Type",
                    Params =
                    {
                        ["Mark"] = "P-01",
                        ["Number"] = "42"
                    }
                }
            },
            PanelStats = new PanelStatsSnapshot
            {
                PanelsOnView = 401,
                PanelsWithSegment = 200,
                PanelsWithNumber = 201,
                PanelsWithMark = 202,
                PanelTagsOnView = 203
            }
        };

        var json = SnapshotJsonSerializer.Serialize(snapshot);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var panel = root.GetProperty("curtainPanels")[0];
        var stats = root.GetProperty("panelStats");

        await Assert.That(root.GetProperty("curtainPanelsCapped").GetBoolean()).IsTrue();
        await Assert.That(panel.GetProperty("id").GetInt64()).IsEqualTo(30);
        await Assert.That(panel.GetProperty("familyName").GetString()).IsEqualTo("Panel Family");
        await Assert.That(panel.GetProperty("typeName").GetString()).IsEqualTo("Panel Type");
        await Assert.That(panel.GetProperty("params").GetProperty("Mark").GetString()).IsEqualTo("P-01");
        await Assert.That(panel.GetProperty("params").GetProperty("Number").GetString()).IsEqualTo("42");
        await Assert.That(stats.GetProperty("panelsOnView").GetInt32()).IsEqualTo(401);
        await Assert.That(stats.GetProperty("panelsWithSegment").GetInt32()).IsEqualTo(200);
        await Assert.That(stats.GetProperty("panelsWithNumber").GetInt32()).IsEqualTo(201);
        await Assert.That(stats.GetProperty("panelsWithMark").GetInt32()).IsEqualTo(202);
        await Assert.That(stats.GetProperty("panelTagsOnView").GetInt32()).IsEqualTo(203);
    }
}
