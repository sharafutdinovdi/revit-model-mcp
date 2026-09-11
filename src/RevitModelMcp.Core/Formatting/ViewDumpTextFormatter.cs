using System.Globalization;
using System.Text;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Formatting;

public static class ViewDumpTextFormatter
{
    private const string EmptyValue = "—";

    public static string Format(ViewDumpReport report)
    {
        if (report is null)
        {
            throw new ArgumentNullException(nameof(report));
        }

        var builder = new StringBuilder();
        builder.AppendLine("RevitModelMcp — ВЫГРУЗКА СОСТАВА ВИДОВ");
        builder.AppendLine($"ответил: {report.Responder.DocumentName} | PID {report.Responder.ProcessId} | Revit {report.Responder.RevitVersion}");
        builder.AppendLine($"путь: {report.Responder.DocumentPath}");
        builder.AppendLine($"статус: {report.Status}");
        builder.AppendLine($"прогресс: {report.ProcessedElements} / {report.TotalElements} элементов");
        if (!string.IsNullOrWhiteSpace(report.Message))
        {
            builder.AppendLine($"сообщение: {report.Message}");
        }

        if (report.RejectedJobsWhileBusy > 0)
        {
            builder.AppendLine($"отклонено заданий во время работы: {report.RejectedJobsWhileBusy} (RevitModelMcp занят)");
        }

        builder.AppendLine();
        foreach (var view in report.Views)
        {
            AppendView(builder, view);
        }

        AppendViewCleanup(builder, report);
        return builder.ToString();
    }

    private static void AppendView(StringBuilder builder, ViewDumpView view)
    {
        builder.AppendLine($"ВИД: «{view.RequestedName}»");
        if (view.Status == "not-found" || view.Status == "error")
        {
            builder.AppendLine($"  {view.Error ?? view.Status}");
            builder.AppendLine();
            return;
        }

        var header = view.Header;
        if (header is null)
        {
            builder.AppendLine($"  статус: {view.Status}");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"  тип: {Value(header.Type)}   уровень: {Value(header.Level)}");
        builder.AppendLine($"  масштаб: 1:{header.Scale}   шаблон: {Value(header.Template)}");
        builder.AppendLine($"  дисциплина: {Value(header.Discipline)}   фильтров: {header.FilterCount}   переопределений графики: {header.GraphicOverrideCount}");
        builder.AppendLine($"  элементов всего: {header.ElementCount}");
        builder.AppendLine();
        builder.AppendLine("ПО КАТЕГОРИЯМ");
        foreach (var category in view.Categories)
        {
            builder.AppendLine($"  {category.Category,-28} {category.Count,8} {category.DifferentTypes,6}");
        }

        builder.AppendLine();
        builder.AppendLine("ЭЛЕМЕНТЫ");
        foreach (var element in view.Elements)
        {
            AppendElement(builder, element);
        }

        builder.AppendLine();
    }

    private static void AppendElement(StringBuilder builder, ViewElementDump element)
    {
        var familyAndType = string.IsNullOrWhiteSpace(element.Family) && string.IsNullOrWhiteSpace(element.Type)
            ? EmptyValue
            : $"{Value(element.Family)} / {Value(element.Type)}";
        builder.AppendLine($"id={element.Id} | {Value(element.Category)} | {familyAndType} | {Value(element.Name)}");

        var dimensions = new List<string>();
        AddNumber(dimensions, "длина", element.LengthMm, "мм");
        AddNumber(dimensions, "толщина", element.ThicknessMm, "мм");
        AddNumber(dimensions, "площадь", element.AreaM2, "м²");
        AddNumber(dimensions, "объём", element.VolumeM3, "м³");
        if (!string.IsNullOrWhiteSpace(element.Level))
        {
            dimensions.Insert(0, $"уровень={element.Level}");
        }

        if (dimensions.Count > 0)
        {
            builder.AppendLine($"  {string.Join("   ", dimensions)}");
        }

        if (element.ProfileParameters.Count > 0)
        {
            builder.AppendLine("  " + string.Join(
                "   ",
                element.ProfileParameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}")));
        }

        var context = new List<string>();
        if (!string.IsNullOrWhiteSpace(element.Workset))
        {
            context.Add($"раб.набор={element.Workset}");
        }

        if (!string.IsNullOrWhiteSpace(element.Phase))
        {
            context.Add($"фаза={element.Phase}");
        }

        if (element.HasWarnings)
        {
            context.Add("предупреждение Revit=да");
        }

        if (context.Count > 0)
        {
            builder.AppendLine($"  {string.Join("   ", context)}");
        }
    }

    private static void AppendViewCleanup(StringBuilder builder, ViewDumpReport report)
    {
        builder.AppendLine("ОТКРЫТЫЕ ВИДЫ");
        builder.AppendLine($"  исходный активный вид восстановлен: {FormatBoolean(report.OriginalViewRestored)}");
        builder.AppendLine($"  открыто RevitModelMcp: {FormatNames(report.OpenedViews)}");
        builder.AppendLine($"  закрыто RevitModelMcp: {FormatNames(report.ClosedViews)}");
        builder.AppendLine("  виды, открытые пользователем до запуска, не закрывались");
    }

    private static void AddNumber(List<string> values, string name, double? value, string unit)
    {
        if (value.HasValue)
        {
            values.Add($"{name}={value.Value.ToString("0.###", CultureInfo.InvariantCulture)} {unit}");
        }
    }

    private static string Value(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? EmptyValue : value!;
    }

    private static string FormatBoolean(bool? value)
    {
        return value.HasValue ? (value.Value ? "да" : "нет") : "ещё нет";
    }

    private static string FormatNames(IReadOnlyCollection<string> names)
    {
        return names.Count == 0 ? EmptyValue : string.Join(", ", names);
    }
}
