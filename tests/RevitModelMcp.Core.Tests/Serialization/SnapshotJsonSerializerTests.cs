using System.Text.Json;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Serialization;

public sealed class SnapshotJsonSerializerTests
{
    [Test]
    public async Task Serialize_InstanceStatus_PreservesChannelContract()
    {
        var status = new InstanceStatus
        {
            ProcessId = 4242,
            RevitVersion = "2023",
            DocumentTitle = "SampleModel",
            DocumentPath = @"C:\\Models\\SampleModel.rvt",
            UpdatedUtc = "2026-08-17T09:15:30.0000000Z"
        };

        using var json = JsonDocument.Parse(InstanceStatusJsonSerializer.Serialize(status));
        var root = json.RootElement;

        await Assert.That(root.GetProperty("processId").GetInt32()).IsEqualTo(4242);
        await Assert.That(root.GetProperty("revitVersion").GetString()).IsEqualTo("2023");
        await Assert.That(root.GetProperty("documentTitle").GetString()).IsEqualTo("SampleModel");
        await Assert.That(root.GetProperty("documentPath").GetString()).IsEqualTo(@"C:\\Models\\SampleModel.rvt");
        await Assert.That(root.GetProperty("updatedUtc").GetString()).IsEqualTo("2026-08-17T09:15:30.0000000Z");
    }

    [Test]
    public async Task Serialize_SectionInventory_UsesExactJsonShape()
    {
        var snapshot = new Snapshot
        {
            Responder = new ResponderInfo { DocumentName = "Customer.rvt", ProcessId = 4242 },
            AllSectionViews =
            {
                new SectionViewSnapshot
                {
                    Id = 42,
                    Name = "ACP Section (1)",
                    TemplateName = "ACP Template",
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
                    Prefix = "ACP"
                }
            },
            DuplicateBaseNames =
            {
                new DuplicateBaseNameSnapshot { BaseName = "ACP Section", Count = 2 }
            }
        };

        var json = SnapshotJsonSerializer.Serialize(snapshot);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var section = root.GetProperty("allSectionViews")[0];
        var crop = section.GetProperty("cropBboxMm");
        var duplicate = root.GetProperty("duplicateBaseNames")[0];

        await Assert.That(section.GetProperty("id").GetInt64()).IsEqualTo(42);
        await Assert.That(section.GetProperty("name").GetString()).IsEqualTo("ACP Section (1)");
        await Assert.That(section.GetProperty("templateName").GetString()).IsEqualTo("ACP Template");
        await Assert.That(section.GetProperty("scale").GetInt32()).IsEqualTo(50);
        await Assert.That(section.GetProperty("cropActive").GetBoolean()).IsTrue();
        await Assert.That(section.GetProperty("isNameDuplicate").GetBoolean()).IsTrue();
        await Assert.That(section.GetProperty("prefix").GetString()).IsEqualTo("ACP");
        await Assert.That(crop.GetProperty("minX").GetDouble()).IsEqualTo(1);
        await Assert.That(crop.GetProperty("maxZ").GetDouble()).IsEqualTo(6);
        await Assert.That(duplicate.GetProperty("baseName").GetString()).IsEqualTo("ACP Section");
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
                        ["QIC_NUMBER"] = "42"
                    }
                }
            },
            PanelStats = new PanelStatsSnapshot
            {
                PanelsOnView = 401,
                PanelsWithQicSegment = 200,
                PanelsWithQicNumber = 201,
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
        await Assert.That(panel.GetProperty("params").GetProperty("QIC_NUMBER").GetString()).IsEqualTo("42");
        await Assert.That(stats.GetProperty("panelsOnView").GetInt32()).IsEqualTo(401);
        await Assert.That(stats.GetProperty("panelsWithQicSegment").GetInt32()).IsEqualTo(200);
        await Assert.That(stats.GetProperty("panelsWithQicNumber").GetInt32()).IsEqualTo(201);
        await Assert.That(stats.GetProperty("panelsWithMark").GetInt32()).IsEqualTo(202);
        await Assert.That(stats.GetProperty("panelTagsOnView").GetInt32()).IsEqualTo(203);
    }
}
