using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal sealed partial class SnapshotCollector
{
    private readonly UIApplication? _uiApplication;
    private readonly UIDocument? _uiDocument;
    private readonly Document? _document;
    private readonly View? _view;
    private readonly Snapshot _snapshot = new();

    private SnapshotCollector(UIApplication? uiApplication)
    {
        _uiApplication = uiApplication;
        _uiDocument = _uiApplication?.ActiveUIDocument;
        _document = _uiDocument?.Document;
        _view = _document?.ActiveView;
    }

    public static Snapshot Capture(UIApplication? uiApplication, DateTimeOffset localNow)
    {
        var collector = new SnapshotCollector(uiApplication);
        if (uiApplication is not null)
        {
            collector._snapshot.Responder = ReadCommandReader.ReadResponder(uiApplication);
        }
        collector.TryCapture("snapshot metadata", () => collector.CaptureMetadata(localNow));
        collector.TryCapture("snapshot active view", collector.CaptureActiveView);
        collector.TryCapture("snapshot regions", collector.CaptureRegions);
        collector.TryCapture("snapshot elements on view", collector.CaptureElementsOnView);
        collector.TryCapture("snapshot annotations", collector.CaptureAnnotations);
        collector.TryCapture("snapshot curtain panels", collector.CaptureCurtainPanels);
        collector.TryCapture("snapshot selection", collector.CaptureSelection);
        collector.TryCapture("snapshot model counts", collector.CaptureModelCounts);
        collector.TryCapture("snapshot section view inventory", collector.CaptureSectionViewInventory);
        collector.TryCapture("snapshot duplicate base names", collector.CaptureDuplicateBaseNames);
        return collector._snapshot;
    }

    private void CaptureMetadata(DateTimeOffset localNow)
    {
        var metadata = _snapshot.Meta;
        metadata.Utc = localNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        metadata.Local = localNow.ToString("O", CultureInfo.InvariantCulture);

        TryCapture("snapshot metadata Revit version", () => metadata.RevitVersion = _uiApplication?.Application?.VersionNumber);
        TryCapture("snapshot metadata document title", () => metadata.DocumentTitle = _document?.Title);
        TryCapture("snapshot metadata document path", () => metadata.DocumentPath = _document?.PathName);
        TryCapture("snapshot metadata active view id", () => metadata.ActiveViewId = _view is null ? null : RevitValueReader.GetId(_view.Id));
    }

    private void CaptureActiveView()
    {
        if (_view is null || _document is null)
        {
            return;
        }

        var target = _snapshot.ActiveView;
        TryCapture("snapshot active view name", () => target.Name = _view.Name);
        TryCapture("snapshot active view id", () => target.Id = RevitValueReader.GetId(_view.Id));
        TryCapture("snapshot active view type", () => target.ViewType = _view.ViewType.ToString());
        TryCapture("snapshot active view scale", () => target.Scale = _view.Scale);
        TryCapture("snapshot active view detail level", () => target.DetailLevel = _view.DetailLevel.ToString());
        TryCapture("snapshot active view section state", () => target.IsSection = _view.ViewType == ViewType.Section);
        TryCapture("snapshot active view crop active", () => target.CropBoxActive = _view.CropBoxActive);
        TryCapture("snapshot active view template", () => CaptureViewTemplate(target));
        TryCapture("snapshot active view crop", () => CaptureCrop(target));
        TryCapture("snapshot active view scope box", () => CaptureScopeBox(target));
        TryCapture("snapshot active view direction", () => target.ViewDirection = RevitValueReader.ToVector(_view.ViewDirection, false));
        TryCapture("snapshot active view origin", () => target.OriginMm = RevitValueReader.ToVector(_view.Origin, true));
        TryCapture("snapshot active view far clip offset", () => target.FarClipOffsetMm = RevitValueReader.GetLengthParameterMm(
            _view,
            BuiltInParameter.VIEWER_BOUND_OFFSET_FAR));
        TryCapture("snapshot active view dependency", () => CaptureDependency(target));
    }

    private void CaptureViewTemplate(ActiveViewSnapshot target)
    {
        var templateId = _view!.ViewTemplateId;
        if (!RevitValueReader.IsValidId(templateId))
        {
            return;
        }

        target.ViewTemplateName = _document!.GetElement(templateId)?.Name;
    }

    private void CaptureCrop(ActiveViewSnapshot target)
    {
        var crop = _view!.CropBox;
        if (crop is not null)
        {
            target.CropBox = RevitValueReader.ToCropBox(crop);
            target.CropWidthMm = RevitValueReader.ToMillimeters(crop.Max.X - crop.Min.X);
            target.CropHeightMm = RevitValueReader.ToMillimeters(crop.Max.Y - crop.Min.Y);
        }

        var manager = _view.GetCropRegionShapeManager();
        target.CustomCropLoops = manager.GetCropShape()?.Count ?? 0;
    }

    private void CaptureScopeBox(ActiveViewSnapshot target)
    {
        var parameter = _view!.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
        var scopeBoxId = parameter?.AsElementId();
        if (!RevitValueReader.IsValidId(scopeBoxId))
        {
            return;
        }

        target.ScopeBoxName = _document!.GetElement(scopeBoxId)?.Name;
    }

    private void CaptureDependency(ActiveViewSnapshot target)
    {
        var primaryId = _view!.GetPrimaryViewId();
        target.IsDependent = RevitValueReader.IsValidId(primaryId);
        if (target.IsDependent == true)
        {
            target.PrimaryViewName = _document!.GetElement(primaryId)?.Name;
        }

        target.DependentsCount = _view.GetDependentViewIds()?.Count ?? 0;
    }

    private void TryCapture(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            PluginLog.Skipped(what, exception);
        }
    }
}
