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
        builder.AppendLine("RevitModelMcp — VIEW CONTENTS DUMP");
        builder.AppendLine($"responder: {report.Responder.DocumentName} | PID {report.Responder.ProcessId} | Revit {report.Responder.RevitVersion}");
        builder.AppendLine($"path: {report.Responder.DocumentPath}");
        builder.AppendLine($"status: {report.Status}");
        builder.AppendLine($"progress: {report.ProcessedElements} / {report.TotalElements} elements");
        if (!string.IsNullOrWhiteSpace(report.Message))
        {
            builder.AppendLine($"message: {report.Message}");
        }

        if (report.RejectedJobsWhileBusy > 0)
        {
            builder.AppendLine($"jobs rejected while busy: {report.RejectedJobsWhileBusy} (RevitModelMcp is busy)");
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
        builder.AppendLine($"VIEW: '{view.RequestedName}'");
        if (view.Status == "not-found" || view.Status == "error")
        {
            builder.AppendLine($"  {view.Error ?? view.Status}");
            builder.AppendLine();
            return;
        }

        var header = view.Header;
        if (header is null)
        {
            builder.AppendLine($"  status: {view.Status}");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"  type: {Value(header.Type)}   level: {Value(header.Level)}");
        builder.AppendLine($"  scale: 1:{header.Scale}   template: {Value(header.Template)}");
        builder.AppendLine($"  discipline: {Value(header.Discipline)}   filters: {header.FilterCount}   graphic overrides: {header.GraphicOverrideCount}");
        builder.AppendLine($"  total elements: {header.ElementCount}");
        builder.AppendLine();
        builder.AppendLine("BY CATEGORY");
        foreach (var category in view.Categories)
        {
            builder.AppendLine($"  {category.Category,-28} {category.Count,8} {category.DifferentTypes,6}");
        }

        builder.AppendLine();
        builder.AppendLine("ELEMENTS");
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
        AddNumber(dimensions, "length", element.LengthMm, "mm");
        AddNumber(dimensions, "thickness", element.ThicknessMm, "mm");
        AddNumber(dimensions, "area", element.AreaM2, "m²");
        AddNumber(dimensions, "volume", element.VolumeM3, "m³");
        if (!string.IsNullOrWhiteSpace(element.Level))
        {
            dimensions.Insert(0, $"level={element.Level}");
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
            context.Add($"workset={element.Workset}");
        }

        if (!string.IsNullOrWhiteSpace(element.Phase))
        {
            context.Add($"phase={element.Phase}");
        }

        if (element.HasWarnings)
        {
            context.Add("Revit warning=yes");
        }

        if (context.Count > 0)
        {
            builder.AppendLine($"  {string.Join("   ", context)}");
        }
    }

    private static void AppendViewCleanup(StringBuilder builder, ViewDumpReport report)
    {
        builder.AppendLine("OPEN VIEWS");
        builder.AppendLine($"  original active view restored: {FormatBoolean(report.OriginalViewRestored)}");
        builder.AppendLine($"  opened by RevitModelMcp: {FormatNames(report.OpenedViews)}");
        builder.AppendLine($"  closed by RevitModelMcp: {FormatNames(report.ClosedViews)}");
        builder.AppendLine("  views opened by the user before the dump were left open");
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
        return value.HasValue ? (value.Value ? "yes" : "no") : "not yet";
    }

    private static string FormatNames(IReadOnlyCollection<string> names)
    {
        return names.Count == 0 ? EmptyValue : string.Join(", ", names);
    }
}
