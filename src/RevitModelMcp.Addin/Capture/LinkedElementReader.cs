using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;
using RevitModelMcp.Core.Units;

namespace RevitModelMcp.Capture;

internal sealed record LinkedReadSource(Document Document, long? InstanceId, string? Name, LinkReadTransform? Transform)
{
    public void Shape(ElementGeometryData row)
    {
        if (Transform is not null) Transform.Apply(row, InstanceId!.Value, Name!);
    }
}

internal static class LinkedElementReader
{
    public static List<LinkedReadSource> Sources(Document host, string? link)
    {
        var selector = LinkSelector.Parse(link);
        if (selector.IsHost) return [new(host, null, null, null)];
        var sources = new List<LinkedReadSource>();
        var matched = selector.All;
        using var instances = new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance));
        var representedTypes = new HashSet<ElementId>();
        foreach (var instance in instances.Cast<RevitLinkInstance>().OrderBy(instance => RevitValueReader.GetId(instance.Id)))
        {
            var id = RevitValueReader.GetId(instance.Id);
            try
            {
                var type = (RevitLinkType)host.GetElement(instance.GetTypeId());
                representedTypes.Add(type.Id);
                if (type.IsNestedLink || !selector.Matches(id, instance.Name, type.Name)) continue;
                matched = true;
                var document = instance.GetLinkDocument();
                if (document is null) throw new InvalidOperationException($"Link document unavailable ({type.GetLinkedFileStatus()}).");
                using var transform = instance.GetTotalTransform();
                sources.Add(new(document, id, instance.Name, new LinkReadTransform(
                    [UnitConverter.FeetToMillimeters(transform.Origin.X), UnitConverter.FeetToMillimeters(transform.Origin.Y), UnitConverter.FeetToMillimeters(transform.Origin.Z)],
                    [transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z],
                    [transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z],
                    [transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z])));
                if (!document.IsWorkshared) continue;
                using var worksets = new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset);
                foreach (var workset in worksets.ToWorksets().Where(workset => !workset.IsOpen))
                    PluginLog.Skipped($"link {id} workset {workset.Name}", new InvalidOperationException("Closed workset: elements are unavailable."));
            }
            catch (Exception exception) { PluginLog.Skipped($"link {id}", exception); }
        }
        using var types = new FilteredElementCollector(host).OfClass(typeof(RevitLinkType));
        foreach (var type in types.Cast<RevitLinkType>())
        {
            try
            {
                if (type.IsNestedLink || representedTypes.Contains(type.Id) || !selector.Matches(-1, type.Name, type.Name)) continue;
                matched = true;
                PluginLog.Skipped($"link type {RevitValueReader.GetId(type.Id)} {type.Name}", new InvalidOperationException($"No available link instance ({type.GetLinkedFileStatus()})."));
            }
            catch (Exception exception) { PluginLog.Skipped("link type", exception); }
        }
        if (!matched) throw new ArgumentException("Link not found. Use a link instance ID or exact instance/type name.", nameof(link));
        return sources;
    }

    public static ElementDetailsData ReadDetails(Document host, ControlJobParseResult job)
    {
        var matches = new List<(LinkedReadSource Source, Element Element)>();
        foreach (var source in Sources(host, job.Link))
        {
            try
            {
                var element = job.UniqueId is not null ? source.Document.GetElement(job.UniqueId)
                    : source.Document.GetElement(Id(job.ElementId!.Value));
                if (element is not null) matches.Add((source, element));
            }
            catch (Exception exception) { PluginLog.Skipped($"link {source.InstanceId} element lookup", exception); }
        }
        if (matches.Count == 0) throw new ArgumentException("Element not found in the selected document(s).");
        if (matches.Count > 1) throw new ArgumentException("Element matches several link instances; select one link instance ID.");
        var match = matches[0];
        try
        {
            var reader = new ViewElementReader(match.Source.Document, ReadCommandReader.ReadWarningElementIds(match.Source.Document));
            var result = reader.ReadDetails(match.Element);
            match.Source.Shape(result);
            return result;
        }
        catch (Exception exception)
        {
            PluginLog.Skipped($"link {match.Source.InstanceId} element details", exception);
            throw;
        }
    }

    public static ElementId Id(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }
}
