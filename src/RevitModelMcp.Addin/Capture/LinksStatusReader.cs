using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class LinksStatusReader
{
    public static LinksStatusData Read(Document document, ControlJobContract job)
    {
        const int listLimit = 100;
        var result = new LinksStatusData { ListLimit = listLimit };
        var rvtInstances = Collect<RevitLinkInstance>(document).ToLookup(instance => instance.GetTypeId());
        var imports = Collect<ImportInstance>(document).ToLookup(instance => instance.GetTypeId());
        var images = Collect<ImageInstance>(document).ToLookup(instance => instance.GetTypeId());
        foreach (var type in Collect<RevitLinkType>(document))
        {
            var item = new RvtLinkStatus { TypeId = RevitValueReader.GetId(type.Id), Status = "Other", PathType = "Unknown" };
            try
            {
                item.Name = type.Name;
                item.Status = Status(type.GetLinkedFileStatus().ToString());
                item.Nested = type.IsNestedLink;
                item.Instances = rvtInstances[type.Id].Count();
                item.Pinned = rvtInstances[type.Id].Any(instance => instance.Pinned);
                var reference = ExternalFileUtils.GetExternalFileReference(document, type.Id);
                item.Path = Path(reference);
                var modelPath = reference.GetAbsolutePath();
                item.PathType = modelPath.CloudPath ? "Cloud" : modelPath.ServerPath ? "Server"
                    : reference.PathType.ToString() is "Absolute" or "Relative" ? reference.PathType.ToString() : "Unknown";
            }
            catch (Exception exception) { item.Status = "Other"; item.Error = exception.Message; }
            result.Summary.Rvt++;
            if (item.Status == "Loaded") result.Summary.RvtLoaded++;
            if (result.RvtLinks.Count < listLimit) result.RvtLinks.Add(item);
        }
        foreach (var type in Collect<CADLinkType>(document))
        {
            var item = new CadLinkStatus { TypeId = RevitValueReader.GetId(type.Id), Status = "Other" };
            try
            {
                item.Name = type.Name;
                var instances = imports[type.Id].ToList();
                item.Instances = instances.Count;
                item.IsLinked = instances.Any(instance => instance.IsLinked) || type.IsExternalFileReference();
                item.ViewSpecific = instances.Any(instance => instance.ViewSpecific);
                if (item.IsLinked)
                {
                    var reference = ExternalFileUtils.GetExternalFileReference(document, type.Id);
                    item.Status = Status(reference.GetLinkedFileStatus().ToString());
                    item.Path = Path(reference);
                }
                else item.Status = "Loaded";
            }
            catch (Exception exception) { item.Status = "Other"; item.Error = exception.Message; }
            result.Summary.Cad++;
            if (result.CadLinks.Count < listLimit) result.CadLinks.Add(item);
        }
        result.Summary.CadImports = imports.SelectMany(group => group).Count(instance => !instance.IsLinked);
        foreach (var type in Collect<ImageType>(document))
        {
            var item = new ImageLinkStatus { TypeId = RevitValueReader.GetId(type.Id), Status = "Other" };
            try
            {
                item.Name = type.Name;
                item.Instances = images[type.Id].Count();
                item.Path = type.Path;
                item.Status = type.Status.ToString();
            }
            catch (Exception exception) { item.Status = "Other"; item.Error = exception.Message; }
            result.Summary.Images++;
            if (result.Images.Count < listLimit) result.Images.Add(item);
        }
        return result;
    }

    private static List<T> Collect<T>(Document document) where T : Element
    {
        using var collector = new FilteredElementCollector(document).OfClass(typeof(T));
        return collector.Cast<T>().OrderBy(element => RevitValueReader.GetId(element.Id)).ToList();
    }

    private static string Path(ExternalFileReference reference) =>
        ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());

    private static string Status(string status) => status is
        "Loaded" or "Unloaded" or "NotFound" or "LocallyUnloaded" or "InClosedWorkset" ? status : "Other";
}
