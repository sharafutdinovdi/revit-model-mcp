using System.Text;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Formatting;

public static class SnapshotSummaryFormatter
{
    private const int PanelCaptureLimit = 400;
    private const int PanelSummaryLimit = 25;
    private const int RegionLimit = 30;

    private static readonly string[] PanelParameterNames =
    {
        "Mark",
        "Segment",
        "Number",
        "Zone Code",
        "NameOverride"
    };

    public static string Format(Snapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var builder = new StringBuilder();
        builder.AppendLine("RevitModelMcp snapshot");
        builder.AppendLine($"Responder: {snapshot.Responder.DocumentName} | PID {snapshot.Responder.ProcessId} | Revit {snapshot.Responder.RevitVersion}");
        builder.AppendLine($"Path: {snapshot.Responder.DocumentPath}");
        builder.AppendLine($"UTC: {snapshot.Meta.Utc ?? "n/a"}");
        builder.AppendLine($"Document: {snapshot.Meta.DocumentTitle ?? "n/a"}");
        builder.AppendLine($"View: {snapshot.ActiveView.Name ?? "n/a"} ({snapshot.ActiveView.Id?.ToString() ?? "n/a"})");
        builder.AppendLine();
        builder.AppendLine("Counts");
        builder.AppendLine($"Regions on view: {snapshot.RegionCount}");
        builder.AppendLine($"Regions in model: {snapshot.ModelCounts.TotalRegions}");
        builder.AppendLine($"Selected: {snapshot.Selection.Count}");
        builder.AppendLine($"Annotations: {snapshot.Annotations.Count}");
        builder.AppendLine($"Coordination views: {snapshot.ModelCounts.CoordinationViews}");
        builder.AppendLine($"Construction views: {snapshot.ModelCounts.ConstructionViews}");
        builder.AppendLine($"Sheets: {snapshot.ModelCounts.Sheets}");
        builder.AppendLine($"Sections: {snapshot.ModelCounts.SectionViews}");
        builder.AppendLine($"Section inventory: {snapshot.AllSectionViews.Count}");
        builder.AppendLine($"Duplicate base view names: {snapshot.DuplicateBaseNames.Count}");
        builder.AppendLine();
        builder.AppendLine($"Regions (first {RegionLimit})");

        foreach (var region in snapshot.Regions.Take(RegionLimit))
        {
            builder.AppendLine(
                $"- {region.Id}: {region.FamilyName ?? "n/a"} / {region.TypeName ?? "n/a"}; mark={region.Mark ?? "n/a"}");
        }

        builder.AppendLine();
        builder.AppendLine("Curtain panels");
        builder.AppendLine(
            $"panels: {snapshot.PanelStats.PanelsOnView} | Segment: {snapshot.PanelStats.PanelsWithSegment} | Number: {snapshot.PanelStats.PanelsWithNumber} | Mark: {snapshot.PanelStats.PanelsWithMark} | tags: {snapshot.PanelStats.PanelTagsOnView}");
        if (snapshot.CurtainPanelsCapped)
        {
            builder.AppendLine($"capture capped at {PanelCaptureLimit} panels");
        }

        foreach (var panel in snapshot.CurtainPanels.Take(PanelSummaryLimit))
        {
            builder.AppendLine(
                $"- {panel.Id} | {panel.FamilyName ?? "n/a"}/{panel.TypeName ?? "n/a"} | {FormatPanelParameters(panel.Params)}");
        }

        builder.AppendLine();
        builder.AppendLine("Selection");
        foreach (var element in snapshot.Selection)
        {
            builder.AppendLine(
                $"- {element.Id}: {element.Category ?? "n/a"}; {element.FamilyName ?? "n/a"} / {element.TypeName ?? "n/a"}; mark={element.Mark ?? "n/a"}");
        }

        builder.AppendLine();
        builder.AppendLine("Duplicate base view names");
        foreach (var duplicate in snapshot.DuplicateBaseNames)
        {
            builder.AppendLine($"- {duplicate.BaseName ?? "n/a"}: {duplicate.Count}");
        }

        return builder.ToString();
    }

    private static string FormatPanelParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var values = PanelParameterNames
            .Where(parameters.ContainsKey)
            .Select(name => $"{name}={parameters[name]}")
            .ToList();

        return values.Count == 0 ? "n/a" : string.Join(", ", values);
    }
}
