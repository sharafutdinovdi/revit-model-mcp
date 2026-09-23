using Autodesk.Revit.DB;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Control;

internal static class AlignLinkDatums
{
    public static ActionResultData Execute(Document document, LinkDatumJobOptions options, bool dryRun,
        ActionCommandExecutor.ActionFailures failures)
    {
        var levelType = ResolveType<LevelType>(document, options.LevelType, ElementTypeGroup.LevelType);
        var gridType = ResolveType<GridType>(document, options.GridType, ElementTypeGroup.GridType);
        var planType = ResolvePlanType(document, options.CreatePlanViews, options.PlanViewType);
        var comparison = LinkDatumReader.Read(document, options);
        var (instance, linkDatums, _, _) = LinkDatumReader.Collect(document, options);
        var linkDocument = instance.GetLinkDocument()!;
        var transform = instance.GetTotalTransform();
        var movedLevels = false;
        using var transaction = new Transaction(document, "revit_align_link_datums");
        if (transaction.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Could not start the action transaction.");
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        try
        {
            foreach (var item in comparison.Items)
            {
                if (item.Status is not ("differs" or "missing_in_host")) continue;
                var linkRecord = linkDatums.First(datum => datum.Id == item.LinkId);
                var host = item.HostId.HasValue ? document.GetElement(ActionCommandExecutor.CreateId(item.HostId.Value)) : null;
                if (host is not null)
                {
                    if (document.IsWorkshared && WorksharingUtils.GetCheckoutStatus(document, host.Id) == CheckoutStatus.OwnedByOtherUser)
                    {
                        item.Status = "skipped";
                        item.Reason = "owned by " + WorksharingUtils.GetWorksharingTooltipInfo(document, host.Id).Owner;
                        continue;
                    }
                    if (host.Pinned && !options.IncludePinned)
                    {
                        item.Status = "skipped";
                        item.Reason = "pinned";
                        continue;
                    }
                    var wasPinned = host.Pinned;
                    if (wasPinned) host.Pinned = false;
                    if (host is Level level)
                    {
                        var difference = transform.OfPoint(new XYZ(0, 0, linkRecord.Elevation)).Z +
                            Feet(options.LevelOffsetMm) - level.ProjectElevation;
                        item.DependentCount = Math.Max(0, level.GetDependentElements(null).Count - 1);
                        ElementTransformUtils.MoveElement(document, level.Id, new XYZ(0, 0, difference));
                        movedLevels = true;
                    }
                    else if (host is Grid grid)
                    {
                        var linkGrid = (Grid)linkDocument.GetElement(ActionCommandExecutor.CreateId(linkRecord.Id));
                        var targetCurve = linkGrid.Curve.CreateTransformed(transform);
                        XYZ movement;
                        if (grid.Curve is Line line && targetCurve is Line targetLine)
                        {
                            var axis = Line.CreateBound(line.GetEndPoint(0), line.GetEndPoint(0) + XYZ.BasisZ);
                            ElementTransformUtils.RotateElement(document, grid.Id, axis,
                                item.AngleDeg!.Value * Math.PI / 180);
                            var targetVector = targetLine.GetEndPoint(1) - targetLine.GetEndPoint(0);
                            var targetDirection = new XYZ(targetVector.X, targetVector.Y, 0).Normalize();
                            var normal = new XYZ(-targetDirection.Y, targetDirection.X, 0);
                            movement = normal.Multiply((targetLine.GetEndPoint(0) - line.GetEndPoint(0)).DotProduct(normal));
                        }
                        else if (grid.Curve is Arc hostArc && targetCurve is Arc targetArc)
                            movement = new XYZ(targetArc.Center.X - hostArc.Center.X,
                                targetArc.Center.Y - hostArc.Center.Y, 0);
                        else throw new InvalidOperationException("Unsupported grid curve changed during alignment.");
                        ElementTransformUtils.MoveElement(document, grid.Id, movement);
                        var scopeId = grid.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST)?.AsElementId();
                        item.ScopeBox = RevitValueReader.IsValidId(scopeId) ? document.GetElement(scopeId)?.Name : null;
                    }
                    if (wasPinned) host.Pinned = true;
                    item.Status = "moved";
                    continue;
                }
                if (!options.CreateMissing)
                {
                    item.Status = "skipped";
                    item.Reason = "creation disabled";
                    continue;
                }
                if (NameInUse(document, item.Kind, item.Name))
                {
                    item.Status = "skipped";
                    item.Reason = "name in use";
                    continue;
                }
                Element created;
                if (item.Kind == "level")
                {
                    var elevation = transform.OfPoint(new XYZ(0, 0, linkRecord.Elevation)).Z + Feet(options.LevelOffsetMm);
                    var level = Level.Create(document, elevation);
                    level.ChangeTypeId(levelType.Id);
                    created = level;
                    if (planType is not null) ViewPlan.Create(document, planType.Id, level.Id);
                }
                else
                {
                    var linkGrid = (Grid)linkDocument.GetElement(ActionCommandExecutor.CreateId(linkRecord.Id));
                    var curve = linkGrid.Curve.CreateTransformed(transform);
                    var grid = curve switch
                    {
                        Line line => Grid.Create(document, line),
                        Arc arc => Grid.Create(document, arc),
                        _ => throw new ArgumentException("Unsupported grid curve.")
                    };
                    grid.ChangeTypeId(gridType.Id);
                    created = grid;
                }
                created.Name = item.Name;
                item.HostId = RevitValueReader.GetId(created.Id);
                item.Workset = document.IsWorkshared ? document.GetWorksetTable().GetWorkset(created.WorksetId)?.Name : null;
                item.Status = "created";
            }
            document.Regenerate();
            var reread = LinkDatumReader.Read(document, options);
            foreach (var item in comparison.Items.Where(item => item.Status == "moved" && item.Kind == "level"))
            {
                var level = (Level)document.GetElement(ActionCommandExecutor.CreateId(item.HostId!.Value));
                item.After = new DatumElevation { ElevationMm = Math.Round(Mm(level.ProjectElevation), 1) };
            }
            if (movedLevels)
                comparison.Warning = "Elements hosted on moved levels moved with them.";
            if (comparison.Items.Any(item => item.Status is "moved" or "created" &&
                reread.Items.All(checkedItem => checkedItem.LinkId != item.LinkId || checkedItem.Status != "aligned")))
                comparison.Warning = (comparison.Warning is null ? "" : comparison.Warning + " ") +
                    "Some aligned datums did not verify within tolerance.";
            comparison.UpdateSummary();
            if (dryRun)
            {
                if (transaction.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("Could not roll back the dry run.");
            }
            else if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException(failures.Message ?? "The action transaction was rolled back.");
            var result = new ActionResultData
            {
                Link = comparison.Link,
                ToleranceMm = comparison.ToleranceMm,
                LevelOffsetMm = comparison.LevelOffsetMm,
                Items = comparison.Items,
                Summary = comparison.Summary,
                Warning = comparison.Warning,
                DryRun = dryRun,
                RolledBack = dryRun
            };
            if (!dryRun)
            {
                try { LinkDatumReader.Read(document, options); }
                catch (Exception exception)
                {
                    result.Verification = new ActionVerification { Error = "Post-commit verification failed: " + exception.Message };
                }
            }
            return result;
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static T ResolveType<T>(Document document, string? name, ElementTypeGroup group) where T : ElementType
    {
        using var collector = new FilteredElementCollector(document).OfClass(typeof(T));
        var types = collector.Cast<T>().ToList();
        var type = name is null ? document.GetElement(document.GetDefaultElementTypeId(group)) as T
            : types.FirstOrDefault(candidate => candidate.Name == name);
        return type ?? throw new ArgumentException($"Unknown {typeof(T).Name} '{name}'.");
    }

    private static bool NameInUse(Document document, string kind, string name)
    {
        using var collector = new FilteredElementCollector(document).OfClass(kind == "level" ? typeof(Level) : typeof(Grid));
        return collector.Cast<Element>().Any(element => element.Name == name);
    }

    private static ViewFamilyType? ResolvePlanType(Document document, bool create, string? name)
    {
        if (!create) return null;
        using var collector = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType));
        var type = collector.Cast<ViewFamilyType>().FirstOrDefault(candidate =>
            candidate.ViewFamily == ViewFamily.FloorPlan && (name is null || candidate.Name == name));
        return type ?? throw new ArgumentException($"Unknown floor-plan type '{name}'.");
    }

    private static double Feet(double millimeters) => UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
    private static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
}
