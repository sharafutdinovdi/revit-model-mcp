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

        await Assert.That(text).Contains("ВИД: «СПП в ГНС 1-й этаж»");
        await Assert.That(text).Contains("тип: FloorPlan   уровень: 00_1_этаж_основной");
        await Assert.That(text).Contains("Помещения");
        await Assert.That(text).Contains("id=11327511 | Помещения | — | Коридор");
        await Assert.That(text).Contains("площадь=48.2 м²");
        await Assert.That(text).Contains("ADSK_Номер корпуса=1");
        await Assert.That(text).Contains("RUS_Area=48.200");
        await Assert.That(text).Contains("предупреждение Revit=да");
        await Assert.That(text).Contains("виды, открытые пользователем до запуска, не закрывались");
    }

    [Test]
    public async Task Format_MissingView_ReportsErrorAndContinuesWithNextView()
    {
        var report = CreatePreparedReport();
        report.Views.Insert(0, ViewDumpView.Missing("Несуществующий вид"));

        var text = ViewDumpTextFormatter.Format(report);

        await Assert.That(text).Contains("ВИД: «Несуществующий вид»");
        await Assert.That(text).Contains("Вид «Несуществующий вид» не найден.");
        await Assert.That(text).Contains("ВИД: «СПП в ГНС 1-й этаж»");
    }

    internal static ViewDumpReport CreatePreparedReport()
    {
        return new ViewDumpReport
        {
            Status = "completed",
            ProcessedElements = 1,
            TotalElements = 1,
            OriginalViewRestored = true,
            OpenedViews = { "СПП в ГНС 1-й этаж" },
            ClosedViews = { "СПП в ГНС 1-й этаж" },
            Views =
            {
                new ViewDumpView
                {
                    RequestedName = "СПП в ГНС 1-й этаж",
                    Status = "completed",
                    Header = new ViewDumpHeader
                    {
                        Name = "СПП в ГНС 1-й этаж",
                        Type = "FloorPlan",
                        Level = "00_1_этаж_основной",
                        Scale = 100,
                        Template = "СПП в ГНС",
                        Discipline = "Architecture",
                        FilterCount = 2,
                        GraphicOverrideCount = 1,
                        ElementCount = 1
                    },
                    Categories =
                    {
                        new ViewCategorySummary
                        {
                            Category = "Помещения",
                            Count = 1,
                            DifferentTypes = 1
                        }
                    },
                    Elements =
                    {
                        new ViewElementDump
                        {
                            Id = 11327511,
                            Category = "Помещения",
                            Name = "Коридор",
                            Level = "00_1_этаж",
                            AreaM2 = 48.2,
                            Workset = "АР_Помещения",
                            Phase = "Новая",
                            HasWarnings = true,
                            ProfileParameters =
                            {
                                ["ADSK_Номер корпуса"] = "1",
                                ["RUS_Area"] = "48.200"
                            }
                        }
                    }
                }
            }
        };
    }
}
