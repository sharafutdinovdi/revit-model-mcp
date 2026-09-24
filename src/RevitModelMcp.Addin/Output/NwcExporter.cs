using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Control;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Export;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Output;

internal static class NwcExporter
{
    internal static ActionResultData Execute(Document document, ActionJobContract action)
    {
        if (document.IsFamilyDocument) throw new ArgumentException("NWC export requires a project document.");
        if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            throw new InvalidOperationException($"Navisworks exporter is not available in Revit {document.Application.VersionNumber} on this workstation (not installed or failed to load at startup).");

        var job = action.Nwc;
        NwcPathValidator.Validate(job.Path);
        var xmlValues = job.SettingsXml is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(NwcSettingsXml.ReadFile(job.SettingsXml).Values);
        foreach (var (key, value) in xmlValues)
        {
            if (job.ExplicitOptions.Contains(key)) continue;
            switch (key)
            {
                case "scope": job.Scope = (string)value; break;
                case "coordinates": job.Coordinates = (string)value; break;
                case "parameters": job.Parameters = (string)value; break;
                case "export_element_ids": job.ExportElementIds = (bool)value; break;
                case "convert_element_properties": job.ConvertElementProperties = (bool)value; break;
                case "export_parts": job.ExportParts = (bool)value; break;
                case "export_room_as_attribute": job.ExportRoomAsAttribute = (bool)value; break;
                case "export_room_geometry": job.ExportRoomGeometry = (bool)value; break;
                case "convert_lights": job.ConvertLights = (bool)value; break;
                case "convert_linked_cad_formats": job.ConvertLinkedCadFormats = (bool)value; break;
                case "export_links": job.ExportLinks = (bool)value; break;
                case "export_urls": job.ExportUrls = (bool)value; break;
                case "divide_file_into_levels": job.DivideFileIntoLevels = (bool)value; break;
                case "find_missing_materials": job.FindMissingMaterials = (bool)value; break;
                case "faceting_factor": job.FacetingFactor = Convert.ToDouble(value); break;
            }
        }
        if (job.Scope is not ("model" or "view" or "selection")) throw new ArgumentException("Invalid NWC scope.");
        if (job.Scope == "selection" && action.ElementIds.Count == 0) throw new ArgumentException("elementIds must be non-empty for scope=selection.");
        if (job.FacetingFactor is <= 0 or > 100 || double.IsNaN(job.FacetingFactor) || double.IsInfinity(job.FacetingFactor)) throw new ArgumentException("facetingFactor must be greater than 0 and at most 100.");
        var folder = Path.GetDirectoryName(job.Path)!;
        if (!Directory.Exists(folder)) throw new ArgumentException("path parent directory does not exist.");
        var targetExists = File.Exists(job.Path);
        if (targetExists && !job.Overwrite) throw new ArgumentException("path already exists; set overwrite=true.");

        View3D? view = null;
        if (job.Scope == "view")
        {
            if (long.TryParse(job.View, out var viewId) && viewId > 0)
            {
                try
                {
                    view = ActionCommandExecutor.CreateId(viewId).ToElement(document) as View3D;
                }
                catch (OverflowException)
                {
                    // Revit 2022-2023 element IDs are 32-bit; the text may still be a view name.
                }
            }
            if (view is null)
            {
                using var views = document.CollectElements().OfClass<View3D>();
                view = views.Cast<View3D>().FirstOrDefault(candidate => candidate.Name == job.View);
            }
            if (view is null || view.IsTemplate)
                throw new ArgumentException("view must identify a non-template 3D view.");
        }
        var ids = job.Scope == "selection" ? ActionCommandExecutor.ResolveIds(document, action.ElementIds) : [];

        var result = new ActionResultData
        {
            Path = job.Path, Scope = job.Scope, DryRun = action.DryRun,
            View = view is null ? null : new NwcViewResult { Id = RevitValueReader.GetId(view.Id), Name = view.Name },
            ElementCount = job.Scope == "selection" ? ids.Count : null,
            ExporterAvailable = true,
            PathChecks = new NwcPathChecks { ParentExists = true, TargetExists = targetExists },
            Options = new NwcOptionsResult
            {
                Scope = job.Scope, View = job.View,
                ElementIds = job.Scope == "selection" ? action.ElementIds : null,
                Coordinates = job.Coordinates, Parameters = job.Parameters,
                ExportElementIds = job.ExportElementIds, ConvertElementProperties = job.ConvertElementProperties,
                ExportParts = job.ExportParts, ExportRoomAsAttribute = job.ExportRoomAsAttribute,
                ExportRoomGeometry = job.ExportRoomGeometry, ConvertLights = job.ConvertLights,
                ConvertLinkedCadFormats = job.ConvertLinkedCadFormats, ExportLinks = job.ExportLinks,
                ExportUrls = job.ExportUrls, DivideFileIntoLevels = job.DivideFileIntoLevels,
                FindMissingMaterials = job.FindMissingMaterials, FacetingFactor = job.FacetingFactor,
                Sources = new[] { "scope", "view", "element_ids", "coordinates", "parameters", "export_element_ids",
                    "convert_element_properties", "export_parts", "export_room_as_attribute", "export_room_geometry",
                    "convert_lights", "convert_linked_cad_formats", "export_links", "export_urls",
                    "divide_file_into_levels", "find_missing_materials", "faceting_factor" }
                    .ToDictionary(key => key, key => key == "view" ? (job.View is null ? "default" : "argument")
                        : key == "element_ids" ? (action.ElementIds.Count == 0 ? "default" : "argument")
                        : job.ExplicitOptions.Contains(key) ? "argument" : xmlValues.ContainsKey(key) ? "xml" : "default")
            },
            Overwritten = false
        };
        if (action.DryRun) return result;
        if (document.IsModifiable) throw new InvalidOperationException("NWC export requires no open transaction.");

        using var options = new NavisworksExportOptions
        {
            ExportScope = job.Scope switch
            {
                "view" => NavisworksExportScope.View,
                "selection" => NavisworksExportScope.SelectedElements,
                _ => NavisworksExportScope.Model
            },
            ViewId = view?.Id ?? ElementId.InvalidElementId,
            Coordinates = job.Coordinates == "shared" ? NavisworksCoordinates.Shared : NavisworksCoordinates.Internal,
            Parameters = job.Parameters switch
            {
                "elements" => NavisworksParameters.Elements,
                "none" => NavisworksParameters.None,
                _ => NavisworksParameters.All
            },
            ExportElementIds = job.ExportElementIds,
            ConvertElementProperties = job.ConvertElementProperties,
            ExportParts = job.ExportParts,
            ExportRoomAsAttribute = job.ExportRoomAsAttribute,
            ExportRoomGeometry = job.ExportRoomGeometry,
            ConvertLights = job.ConvertLights,
            ConvertLinkedCADFormats = job.ConvertLinkedCadFormats,
            ExportLinks = job.ExportLinks,
            ExportUrls = job.ExportUrls,
            DivideFileIntoLevels = job.DivideFileIntoLevels,
            FindMissingMaterials = job.FindMissingMaterials,
            FacetingFactor = job.FacetingFactor
        };
        options.SetSelectedElementIds(ids);
        var temporaryPath = job.Overwrite ? Path.Combine(folder, $".{Guid.NewGuid():N}.nwc") : job.Path;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            document.Export(folder, Path.GetFileNameWithoutExtension(temporaryPath), options);
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                throw new InvalidOperationException("Export finished but no file was produced.");
            if (job.Overwrite)
            {
                if (targetExists) File.Replace(temporaryPath, job.Path, null);
                else File.Move(temporaryPath, job.Path);
            }
            using var stream = File.OpenRead(job.Path);
            using var sha256 = SHA256.Create();
            result.Bytes = stream.Length;
            result.Sha256 = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.Overwritten = targetExists;
            return result;
        }
        finally
        {
            if (job.Overwrite && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
