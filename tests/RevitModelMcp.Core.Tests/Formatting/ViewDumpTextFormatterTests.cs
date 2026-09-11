using RevitModelMcp.Core.Formatting;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Formatting;

public sealed class ViewDumpTextFormatterTests
{
    [Test]
    public async Task Format_PreparedReport_ProducesReadableViewDump()
    {
        var report = CreatePreparedReport();

        var text = ViewDumpTextFormatter.Format(report);

        await Assert.That(text).Contains("VIEW: 'Level 1 Plan'");
        await Assert.That(text).Contains("type: FloorPlan   level: Level 1");
        await Assert.That(text).Contains("Rooms");
        await Assert.That(text).Contains("id=11327511 | Rooms | — | Corridor");
        await Assert.That(text).Contains("area=48.2 m²");
        await Assert.That(text).Contains("Project_Building Number=1");
        await Assert.That(text).Contains("Project_Area=48.200");
        await Assert.That(text).Contains("Revit warning=yes");
        await Assert.That(text).Contains("views opened by the user before the dump were left open");
    }

    [Test]
    public async Task Format_MissingView_ReportsErrorAndContinuesWithNextView()
    {
        var report = CreatePreparedReport();
        report.Views.Insert(0, ViewDumpView.Missing("Missing View"));

        var text = ViewDumpTextFormatter.Format(report);

        await Assert.That(text).Contains("VIEW: 'Missing View'");
        await Assert.That(text).Contains("View 'Missing View' was not found.");
        await Assert.That(text).Contains("VIEW: 'Level 1 Plan'");
    }

    internal static ViewDumpReport CreatePreparedReport()
    {
        return new ViewDumpReport
        {
            Status = "completed",
            ProcessedElements = 1,
            TotalElements = 1,
            OriginalViewRestored = true,
            OpenedViews = { "Level 1 Plan" },
            ClosedViews = { "Level 1 Plan" },
            Views =
            {
                new ViewDumpView
                {
                    RequestedName = "Level 1 Plan",
                    Status = "completed",
                    Header = new ViewDumpHeader
                    {
                        Name = "Level 1 Plan",
                        Type = "FloorPlan",
                        Level = "Level 1",
                        Scale = 100,
                        Template = "Gross Building",
                        Discipline = "Architecture",
                        FilterCount = 2,
                        GraphicOverrideCount = 1,
                        ElementCount = 1
                    },
                    Categories =
                    {
                        new ViewCategorySummary
                        {
                            Category = "Rooms",
                            Count = 1,
                            DifferentTypes = 1
                        }
                    },
                    Elements =
                    {
                        new ViewElementDump
                        {
                            Id = 11327511,
                            Category = "Rooms",
                            Name = "Corridor",
                            Level = "Level 1",
                            AreaM2 = 48.2,
                            Workset = "Rooms",
                            Phase = "New Construction",
                            HasWarnings = true,
                            ProfileParameters =
                            {
                                ["Project_Building Number"] = "1",
                                ["Project_Area"] = "48.200"
                            }
                        }
                    }
                }
            }
        };
    }
}
