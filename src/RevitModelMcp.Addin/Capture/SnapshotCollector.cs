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
        collector.TryCapture(() => collector.CaptureMetadata(localNow));
        collector.TryCapture(collector.CaptureActiveView);
        collector.TryCapture(collector.CaptureRegions);
        collector.TryCapture(collector.CaptureElementsOnView);
        collector.TryCapture(collector.CaptureAnnotations);
        collector.TryCapture(collector.CaptureCurtainPanels);
        collector.TryCapture(collector.CaptureSelection);
        collector.TryCapture(collector.CaptureModelCounts);
        collector.TryCapture(collector.CaptureSectionViewInventory);
        collector.TryCapture(collector.CaptureDuplicateBaseNames);
        return collector._snapshot;
    }

    private void CaptureMetadata(DateTimeOffset localNow)
    {
        var metadata = _snapshot.Meta;
        metadata.Utc = localNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        metadata.Local = localNow.ToString("O", CultureInfo.InvariantCulture);

        TryCapture(() => metadata.RevitVersion = _uiApplication?.Application?.VersionNumber);
        TryCapture(() => metadata.DocumentTitle = _document?.Title);
        TryCapture(() => metadata.DocumentPath = _document?.PathName);
        TryCapture(() => metadata.ActiveViewId = _view is null ? null : RevitValueReader.GetId(_view.Id));
    }

    private void CaptureActiveView()
    {
        if (_view is null || _document is null)
        {
            return;
        }

        var target = _snapshot.ActiveView;
        TryCapture(() => target.Name = _view.Name);
        TryCapture(() => target.Id = RevitValueReader.GetId(_view.Id));
        TryCapture(() => target.ViewType = _view.ViewType.ToString());
        TryCapture(() => target.Scale = _view.Scale);
        TryCapture(() => target.DetailLevel = _view.DetailLevel.ToString());
        TryCapture(() => target.IsSection = _view.ViewType == ViewType.Section);
        TryCapture(() => target.CropBoxActive = _view.CropBoxActive);
        TryCapture(() => CaptureViewTemplate(target));
        TryCapture(() => CaptureCrop(target));
        TryCapture(() => CaptureScopeBox(target));
        TryCapture(() => target.ViewDirection = RevitValueReader.ToVector(_view.ViewDirection, false));
        TryCapture(() => target.OriginMm = RevitValueReader.ToVector(_view.Origin, true));
        TryCapture(() => target.FarClipOffsetMm = RevitValueReader.GetLengthParameterMm(
            _view,
            BuiltInParameter.VIEWER_BOUND_OFFSET_FAR));
        TryCapture(() => CaptureDependency(target));
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

    private void TryCapture(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Every snapshot section is best-effort by contract.
        }
    }
}
