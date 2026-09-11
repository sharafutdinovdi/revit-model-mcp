using RevitModelMcp.Core.Formatting;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Formatting;

public sealed class SnapshotSummaryFormatterTests
{
    [Test]
    public async Task Format_MoreThanThirtyRegions_ListsOnlyFirstThirty()
    {
        var snapshot = new Snapshot
        {
            WsRegionCount = 31,
            WsRegions = Enumerable.Range(1, 31)
                .Select(id => new WsRegionSnapshot { Id = id, FamilyName = "WS_Region" })
                .ToList()
        };

        var summary = SnapshotSummaryFormatter.Format(snapshot);

        await Assert.That(summary).Contains("- 30: WS_Region");
        await Assert.That(summary).DoesNotContain("- 31: WS_Region");
    }

    [Test]
    public async Task Format_CurtainPanels_ListsStatsAndOnlyFirstTwentyFivePanels()
    {
        var snapshot = new Snapshot
        {
            CurtainPanelsCapped = true,
            PanelStats = new PanelStatsSnapshot
            {
                PanelsOnView = 401,
                PanelsWithQicSegment = 20,
                PanelsWithQicNumber = 19,
                PanelsWithMark = 18,
                PanelTagsOnView = 17
            },
            CurtainPanels = Enumerable.Range(1, 26)
                .Select(id => new CurtainPanelSnapshot
                {
                    Id = id,
                    FamilyName = "Panel Family",
                    TypeName = "Panel Type",
                    Params =
                    {
                        ["Mark"] = $"M-{id}",
                        ["QIC_SEGMENT"] = "S-1"
                    }
                })
                .ToList()
        };

        var summary = SnapshotSummaryFormatter.Format(snapshot);

        await Assert.That(summary).Contains("Curtain panels");
        await Assert.That(summary).Contains(
            "panels: 401 | QIC_SEGMENT: 20 | QIC_NUMBER: 19 | Mark: 18 | tags: 17");
        await Assert.That(summary).Contains("capture capped at 400 panels");
        await Assert.That(summary).Contains("- 25 | Panel Family/Panel Type | Mark=M-25, QIC_SEGMENT=S-1");
        await Assert.That(summary).DoesNotContain("- 26 | Panel Family/Panel Type");
    }
}
