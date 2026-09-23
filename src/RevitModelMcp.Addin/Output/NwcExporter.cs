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
                FindMissingMaterials = job.FindMissingMaterials, FacetingFactor = job.FacetingFactor
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
