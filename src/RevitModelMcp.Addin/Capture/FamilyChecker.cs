using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Families;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Output;

namespace RevitModelMcp.Capture;

internal static class FamilyChecker
{
    private const double MillimetresPerFoot = 304.8;
    private const double CubicMetresPerCubicFoot = 0.028316846592;

    internal static FamilyCheckData Read(UIApplication application, Document document, ControlJobContract job,
        Func<bool> shouldStop)
    {
        var options = job.FamilyCheck ?? new FamilyCheckOptions();
        var data = new FamilyCheckData { Mode = document.IsFamilyDocument ? "family" : "project" };
        using var temporaryUndo = UndoTracker.BeginTemporary();
        if (document.IsModifiable) throw new InvalidOperationException("Close the document transaction before checking families.");
        if (document.IsFamilyDocument)
        {
            using var suppression = new FamilyAuditReader.FamilySuppression(application, document.Title, data.Skipped);
            var result = CheckDocument(document, options, data, shouldStop);
            data.Families.Add(result);
            if (options.HostTest && !shouldStop()) TestInTemporaryProject(application, document, result, data, shouldStop);
        }
        else
        {
            var selected = FamilyCheckRules.SelectFamilies(FamilyAuditReader.SelectFamilies(document, job.Families!, data.Skipped));
            data.Skipped.AddRange(selected.Skipped.Select(item => new SkippedRead { What = item.Name, Reason = "Family check is limited to 20 families." }));
            foreach (var (name, family) in selected.Assessed)
            {
                if (shouldStop())
                {
                    data.Families.Add(new FamilyCheckFamily { Name = name, Reason = "Cancelled before assessment." });
                    data.Skipped.Add(new SkippedRead { What = name, Reason = "Cancelled before assessment." });
                    continue;
                }
                var reason = family is null ? "not found" : FamilyAuditReader.SkipReason(family);
                if (reason is not null)
                {
                    data.Families.Add(new FamilyCheckFamily { Name = name, Reason = reason });
                    continue;
                }
                using var suppression = new FamilyAuditReader.FamilySuppression(application, name, data.Skipped);
                Document? opened = null;
                FamilyCheckFamily? assessment = null;
                try
                {
                    opened = document.EditFamily(family!);
                    var result = CheckDocument(opened, options, data, shouldStop);
                    assessment = result;
                    result.Name = name;
                    data.Families.Add(result);
                    if (options.HostTest && !shouldStop()) TestPlacement(document, family!, result, data, shouldStop);
                }
                catch (Exception exception)
                {
                    if (assessment is null) data.Families.Add(new FamilyCheckFamily { Name = name, Reason = exception.Message });
                    else assessment.Reason = exception.Message;
                }
                finally
                {
                    if (opened is not null && !opened.Close(false))
                        throw new InvalidOperationException("Revit did not close the checked family.");
                }
            }
        }
        foreach (var family in data.Families)
        {
            if (options.HostTest && family.Types.Any(type => type.Status == "passed" && type.Placed is null))
                family.Reason ??= "Host testing was not assessed for every type.";
            if (family.Types.Any(type => type.Status == "not_assessed"))
                family.Reason ??= "One or more types were not assessed; see skipped diagnostics.";
            family.Status = FamilyCheckRules.Status(family.Checks,
                family.Reason is null && family.Types.Count > 0 && family.Types.All(type => type.Status == "passed"));
        }
        data.SkippedCount = data.Skipped.Count;
        foreach (var severity in new[] { "error", "warning", "info" })
            data.Summary[severity] = data.Families.Sum(family => family.Checks.Count(check => check.Severity == severity));
        return data;
    }

    private static FamilyCheckFamily CheckDocument(Document document, FamilyCheckOptions options, FamilyCheckData data,
        Func<bool> shouldStop)
    {
        var result = new FamilyCheckFamily
        {
            Name = Path.GetFileNameWithoutExtension(document.Title),
            Category = document.OwnerFamily.FamilyCategory?.Name
        };
        var manager = document.FamilyManager;
        var parameters = FamilyAuditReader.ReadParameters(document);
        ReadStructure(document, result, parameters, data);
        using var group = new TransactionGroup(document, "Check family temporarily");
        group.Start();
        try
        {
            var types = manager.Types.Cast<FamilyType>().ToList();
            if (types.Count == 0)
                Commit(document, () => types.Add(manager.NewType("Temporary check type")));
            var names = options.Types ?? types.Select(type => type.Name).ToList();
            foreach (var name in names.Skip(options.MaxTypes))
            {
                result.Types.Add(new FamilyCheckType { Name = name });
                data.Skipped.Add(new SkippedRead { What = $"{result.Name}: {name}", Reason = "max_types limit; type not assessed." });
            }
            foreach (var name in names.Take(options.MaxTypes))
            {
                var assessment = new FamilyCheckType { Name = name };
                result.Types.Add(assessment);
                var type = types.FirstOrDefault(candidate => candidate.Name == name);
                if (type is null || shouldStop())
                {
                    data.Skipped.Add(new SkippedRead { What = name, Reason = type is null ? "Type not found." : "Cancelled." });
                    continue;
                }
                try
                {
                    Commit(document, () => manager.CurrentType = type);
                    var baseline = Measure(document, result, name, ViewDetailLevel.Fine);
                    if (options.Expected?.TryGetValue(name, out var expected) == true && !FamilyCheckRules.ExpectedMatches(baseline.Bounds, expected))
                        Add(result, "expected_mismatch", name, "Regenerated bounds exceed the source tolerance.",
                            evidence: new() { ["actualMm"] = BoundsText(baseline.Bounds), ["toleranceMm"] = Number(expected.ToleranceMm) });
                    if (options.Flex) Flex(document, options, result, assessment, parameters, baseline, shouldStop);
                    var coarse = Measure(document, result, name, ViewDetailLevel.Coarse);
                    if (SimplificationCommon(document) && baseline.Fingerprint == coarse.Fingerprint && FamilyCheckRules.SameGeometry(baseline.Bounds, baseline.Volume, coarse.Bounds, coarse.Volume))
                        Add(result, "coarse_same_as_fine", name, "Coarse and fine visible geometry have equal mesh vertices, bounds, and volume.");
                    ExportPreviews(document, options, result, name, data, shouldStop);
                    assessment.Status = FamilyCheckRules.Status(result.Checks.Where(check => check.Type == name),
                        assessment.Variants.All(variant => variant.Status != "not_assessed"));
                }
                catch (Exception exception)
                {
                    Add(result, "regen_failed", name, exception.Message);
                    assessment.Status = "failed";
                }
            }
        }
        finally
        {
            if (group.RollBack() != TransactionStatus.RolledBack)
                throw new InvalidOperationException("Family check rollback did not complete.");
        }
        return result;
    }

    private static void Commit(Document document, Action change)
    {
        using var transaction = new Transaction(document, "Check family temporarily");
        transaction.Start();
        var failures = new CheckFailures();
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        change();
        document.Regenerate();
        if (transaction.Commit() != TransactionStatus.Committed || failures.HadFailure)
            throw new InvalidOperationException("Regeneration raised a Revit failure or did not commit. " + failures.Message);
    }

    private sealed class CheckFailures : IFailuresPreprocessor
    {
        internal bool HadFailure { get; private set; }
        internal string Message { get; private set; } = string.Empty;
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var messages = accessor.GetFailureMessages();
            HadFailure |= messages.Count > 0;
            Message = string.Join("; ", messages.Select(failure => failure.GetDescriptionText()));
            return messages.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }

    private sealed record Measurement(FamilyCheckBounds Bounds, double Volume, string Fingerprint);

    private static Measurement Measure(Document document, FamilyCheckFamily result, string typeName, ViewDetailLevel detail)
    {
        var solids = new List<Solid>();
        using var geometryOptions = new Options { DetailLevel = detail, IncludeNonVisibleObjects = false };
        var category = document.OwnerFamily.FamilyCategory;
        foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            if (element is GenericForm { IsSolid: false }) continue;
            if (element.Category is null || category is null ||
                element.Category.Id != category.Id && element.Category.Parent?.Id != category.Id) continue;
            if (element is not GenericForm && element is not FamilyInstance) continue;
            var geometry = element.get_Geometry(geometryOptions);
            if (geometry is not null) ReadSolids(geometry, solids);
        }
        if (detail == ViewDetailLevel.Fine && (solids.Count == 0 || !solids.Any(solid => solid.Volume > 1e-12)))
            Add(result, "geometry_empty", typeName, "No visible model-category solid has positive volume.");
        var points = new List<XYZ>();
        var volume = 0.0;
        foreach (var solid in solids)
        {
            if (solid.Volume <= 0) Add(result, "geometry_invalid", typeName, "A model solid has non-positive volume.");
            volume += solid.Volume;
            foreach (Face face in solid.Faces)
                points.AddRange(face.Triangulate().Vertices);
        }
        for (var first = 0; first < solids.Count; first++)
            for (var second = first + 1; second < solids.Count; second++)
            {
                try
                {
                    using var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(solids[first], solids[second], BooleanOperationsType.Intersect);
                    var ratio = intersection.Volume / Math.Max(1e-12, Math.Min(solids[first].Volume, solids[second].Volume));
                    var threshold = FamilyCheckRules.OverlapThreshold(CategoryKey(document));
                    if (threshold.HasValue && ratio > threshold.Value.P90)
                        Add(result, "overlap_high", typeName, "Own-solid intersection exceeds the corpus P90.",
                            evidence: new() { ["ratio"] = Number(ratio), ["p90"] = Number(threshold.Value.P90), ["n"] = threshold.Value.Count.ToString(CultureInfo.InvariantCulture) });
                }
                catch (Exception exception) { Add(result, "geometry_invalid", typeName, exception.Message); }
            }
        var bounds = new FamilyCheckBounds();
        if (points.Count > 0)
        {
            bounds.Width = (points.Max(point => point.X) - points.Min(point => point.X)) * MillimetresPerFoot;
            bounds.Depth = (points.Max(point => point.Y) - points.Min(point => point.Y)) * MillimetresPerFoot;
            bounds.Height = (points.Max(point => point.Z) - points.Min(point => point.Z)) * MillimetresPerFoot;
        }
        var fingerprint = string.Join(";", points.Select(point => $"{Number(Math.Round(point.X, 6))},{Number(Math.Round(point.Y, 6))},{Number(Math.Round(point.Z, 6))}")
            .Distinct().OrderBy(point => point, StringComparer.Ordinal));
        return new Measurement(bounds, volume * CubicMetresPerCubicFoot, fingerprint);
    }

    private static void ReadSolids(GeometryElement geometry, List<Solid> solids)
    {
        foreach (var item in geometry)
        {
            if (item is Solid solid && solid.Faces.Size > 0) solids.Add(solid);
            if (item is GeometryInstance instance) ReadSolids(instance.GetInstanceGeometry(), solids);
        }
    }

    private static void Flex(Document document, FamilyCheckOptions options, FamilyCheckFamily result,
        FamilyCheckType type, List<FamilyParameterData> metadata, Measurement baseline, Func<bool> shouldStop)
    {
        var manager = document.FamilyManager;
        var candidates = manager.Parameters.Cast<FamilyParameter>().Where(parameter => !parameter.IsReadOnly &&
            !parameter.IsDeterminedByFormula && !parameter.IsReporting && parameter.Definition.GetDataType() == SpecTypeId.Length &&
            manager.CurrentType.AsDouble(parameter) is > 0).OrderByDescending(parameter =>
                parameter.Definition is InternalDefinition definition && definition.BuiltInParameter is BuiltInParameter.FAMILY_WIDTH_PARAM or BuiltInParameter.FAMILY_HEIGHT_PARAM)
            .ThenByDescending(parameter => metadata.Any(item => item.Name == parameter.Definition.Name && item.UsedBy.Contains("label"))).Take(2).ToList();
        var variants = options.Variants ?? candidates.Select(parameter => new FamilyCheckVariant
        {
            Parameter = parameter.Definition.Name,
            ValuesMm = new[] { 0.95, 1.05 }.Select(factor => Math.Max(1, Math.Min(100000,
                manager.CurrentType.AsDouble(parameter)!.Value * MillimetresPerFoot * factor))).ToList()
        }).ToList();
        foreach (var variant in variants)
        {
            var parameter = manager.Parameters.Cast<FamilyParameter>().FirstOrDefault(item => item.Definition.Name == variant.Parameter);
            foreach (var value in variant.ValuesMm)
            {
                var trial = new FamilyCheckVariantResult { Parameter = variant.Parameter, ValueMm = value };
                type.Variants.Add(trial);
                if (shouldStop()) continue;
                using var group = new TransactionGroup(document, "Flex family temporarily");
                group.Start();
                try
                {
                    if (parameter is null || parameter.IsReadOnly || parameter.IsDeterminedByFormula || parameter.IsReporting || parameter.Definition.GetDataType() != SpecTypeId.Length)
                    {
                        Add(result, "parameter_no_effect", type.Name, "Parameter cannot be flexed as a writable driving length.", "info");
                        continue;
                    }
                    var original = manager.CurrentType.AsDouble(parameter);
                    Commit(document, () => manager.Set(parameter, value / MillimetresPerFoot));
                    var measured = Measure(document, result, type.Name, ViewDetailLevel.Fine);
                    trial.BboxMm = measured.Bounds;
                    trial.VolumeM3 = measured.Volume;
                    var labelled = metadata.Any(item => item.Name == variant.Parameter && item.UsedBy.Contains("label"));
                    var ambiguous = new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Any();
                    var severity = FamilyCheckRules.NoEffectSeverity(baseline.Bounds, baseline.Volume, measured.Bounds, measured.Volume,
                        labelled, parameter.IsReporting, ambiguous);
                    if (severity is not null && original.HasValue && Math.Abs(value - original.Value * MillimetresPerFoot) > 0.01)
                        Add(result, "parameter_no_effect", type.Name, "Labelled length changed without changing the total bounds or volume.", severity,
                            new() { ["parameter"] = variant.Parameter, ["valueMm"] = Number(value), ["ambiguousNestedGeometry"] = ambiguous.ToString() });
                    trial.Status = severity == "error" || result.Checks.Any(check => check.Type == type.Name && check.Id is "geometry_empty" or "geometry_invalid") ? "failed" : "passed";
                }
                catch (Exception exception)
                {
                    Add(result, "regen_failed", type.Name, exception.Message,
                        evidence: new() { ["parameter"] = variant.Parameter, ["valueMm"] = Number(value) });
                    trial.Status = "failed";
                }
                finally
                {
                    if (group.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Variant rollback did not complete.");
                }
            }
        }
    }

    private static void ReadStructure(Document document, FamilyCheckFamily result, List<FamilyParameterData> parameters, FamilyCheckData data)
    {
        if (FamilyCheckRules.OverlapThreshold(CategoryKey(document)) is null)
            data.Skipped.Add(new SkippedRead { What = result.Name, Reason = "Overlap threshold not assessed: no measured corpus P90 and sample count for this category." });
        var forms = new FilteredElementCollector(document).OfClass(typeof(GenericForm)).Cast<GenericForm>().ToList();
        foreach (var kind in forms.GroupBy(form => form.GetType().Name)) result.Structure[kind.Key] = kind.Count();
        result.Structure["referencePlanes"] = new FilteredElementCollector(document).OfClass(typeof(ReferencePlane)).GetElementCount();
        var labelledDimensions = 0;
        foreach (var dimension in new FilteredElementCollector(document).OfClass(typeof(Dimension)).Cast<Dimension>())
        {
            try { if (dimension.FamilyLabel is not null) labelledDimensions++; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
        }
        result.Structure["labelledDimensions"] = labelledDimensions;
        result.Structure["lockedDimensions"] = new FilteredElementCollector(document).OfClass(typeof(Dimension)).Cast<Dimension>()
            .Count(dimension => dimension.NumberOfSegments > 0
                ? dimension.Segments.Cast<DimensionSegment>().Any(segment => segment.IsLocked)
                : dimension.IsLocked);
        result.Structure["formulas"] = parameters.Count(parameter => !string.IsNullOrWhiteSpace(parameter.Formula));
        result.Structure["nestedFamilies"] = new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).GetElementCount();
        Add(result, "structure", null, "Structure counts are descriptive evidence only.", evidence: result.Structure.ToDictionary(item => item.Key, item => item.Value.ToString(CultureInfo.InvariantCulture)));
        if (new FilteredElementCollector(document).OfClass(typeof(ImportInstance)).Any())
            Add(result, "imported_cad", null, "Family contains imported or linked CAD instances.");
        if (document.OwnerFamily.FamilyCategory?.SubCategories.Size > 0 && forms.Any(form => form.Subcategory is null))
            Add(result, "no_subcategory", null, "Model forms have no subcategory despite defined family subcategories.");
        foreach (var form in forms.Where(form => form.IsSolid))
        {
            var material = form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (material is not null && material.AsElementId() != ElementId.InvalidElementId &&
                document.FamilyManager.GetAssociatedFamilyParameter(material) is null)
                Add(result, "material_not_parameter", null, "A solid has a direct material assignment.");
        }
    }

    private static void ExportPreviews(Document document, FamilyCheckOptions options, FamilyCheckFamily result,
        string type, FamilyCheckData data, Func<bool> shouldStop)
    {
        foreach (var requested in options.Previews.Distinct())
            foreach (var detail in options.DetailLevels.Distinct())
            {
                var what = $"{result.Name}: {type}, {requested}, {detail}";
                if (shouldStop() || data.Families.Sum(family => family.Previews.Count) + result.Previews.Count >= 24)
                {
                    data.Skipped.Add(new SkippedRead { What = what, Reason = "Preview limit (24) or cancellation." });
                    continue;
                }
                try
                {
                    View? view = null;
                    Commit(document, () =>
                    {
                        var views = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Where(candidate => !candidate.IsTemplate).ToList();
                        view = requested switch
                        {
                            "3d" => views.OfType<View3D>().FirstOrDefault(candidate => !candidate.IsPerspective),
                            "plan" => views.FirstOrDefault(candidate => candidate.ViewType == ViewType.FloorPlan),
                            "front" => views.FirstOrDefault(candidate => candidate.ViewType == ViewType.Elevation && candidate.ViewDirection.Y < -0.99),
                            "right" => views.FirstOrDefault(candidate => candidate.ViewType == ViewType.Elevation && candidate.ViewDirection.X > 0.99),
                            _ => null
                        };
                        if (view is null && requested == "3d")
                        {
                            var viewType = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional);
                            view = View3D.CreateIsometric(document, viewType.Id);
                        }
                        if (view is null) throw new InvalidOperationException("No supported family view for this orientation.");
                        if (view is View3D isometric)
                            isometric.SetOrientation(new ViewOrientation3D(new XYZ(10, -10, 10), new XYZ(-1, 1, 2).Normalize(), new XYZ(-1, 1, -1).Normalize()));
                        view.DetailLevel = detail switch { "coarse" => ViewDetailLevel.Coarse, "medium" => ViewDetailLevel.Medium, _ => ViewDetailLevel.Fine };
                    });
                    var image = ViewImageExporter.Export(document, view!, options.ImagePx, true, DateTime.Now);
                    var path = Path.Combine(ChannelDirectory.OutputDirectory, image.FileName);
                    var encoded = Convert.ToBase64String(File.ReadAllBytes(path));
                    var existingBytes = data.Families.Sum(family => family.Previews.Sum(preview => preview.Artifact.Length)) +
                        result.Previews.Sum(preview => preview.Artifact.Length);
                    if (existingBytes + encoded.Length > 512 * 1024)
                    {
                        File.Delete(path);
                        data.Skipped.Add(new SkippedRead { What = what, Reason = "Preview payload budget reached; request fewer images or a smaller image_px." });
                        continue;
                    }
                    result.Previews.Add(new FamilyCheckPreview
                    {
                        Type = type,
                        View = requested,
                        Detail = detail,
                        ArtifactName = image.FileName,
                        Artifact = encoded,
                        WidthPx = image.Width,
                        HeightPx = image.Height
                    });
                    File.Delete(path);
                }
                catch (Exception exception) { data.Skipped.Add(new SkippedRead { What = what, Reason = exception.Message }); }
            }
    }

    private static void TestInTemporaryProject(UIApplication application, Document familyDocument, FamilyCheckFamily result,
        FamilyCheckData data, Func<bool> shouldStop)
    {
        Document? project = null;
        try
        {
            project = application.Application.NewProjectDocument(UnitSystem.Metric);
            var family = familyDocument.LoadFamily(project);
            TestPlacement(project, family, result, data, shouldStop);
        }
        catch (Exception exception)
        {
            result.Reason = "Temporary project could not host the family.";
            data.Skipped.Add(new SkippedRead { What = result.Name, Reason = exception.Message });
        }
        finally
        {
            if (project is not null && !project.Close(false))
                throw new InvalidOperationException("Revit did not close the temporary project.");
        }
    }

    private static void TestPlacement(Document document, Family family, FamilyCheckFamily result, FamilyCheckData data,
        Func<bool> shouldStop)
    {
        foreach (var type in result.Types.Where(type => type.Status != "not_assessed"))
        {
            if (shouldStop()) break;
            var symbol = family.GetFamilySymbolIds().Select(id => document.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault(candidate => candidate.Name == type.Name);
            if (symbol is null)
            {
                data.Skipped.Add(new SkippedRead { What = type.Name, Reason = "No project symbol for this type." });
                continue;
            }
            using var group = new TransactionGroup(document, "Test family placement temporarily");
            group.Start();
            try
            {
                Wall? wall = null;
                Floor? planeHost = null;
                Level? temporaryLevel = null;
                var beforeVolume = 0.0;
                Commit(document, () =>
                {
                    if (!symbol.IsActive) symbol.Activate();
                    var level = Level.Create(document, 0);
                    temporaryLevel = level;
                    var placement = family.FamilyPlacementType;
                    if (placement == FamilyPlacementType.WorkPlaneBased)
                    {
                        var floorType = new FilteredElementCollector(document).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault()
                            ?? throw new NotSupportedException("No floor type for a temporary level-plane host.");
                        var corners = new[] { new XYZ(-10, -10, 0), new XYZ(10, -10, 0), new XYZ(10, 10, 0), new XYZ(-10, 10, 0) };
                        var profile = new CurveLoop();
                        for (var index = 0; index < corners.Length; index++)
                            profile.Append(Line.CreateBound(corners[index], corners[(index + 1) % corners.Length]));
                        planeHost = Floor.Create(document, new List<CurveLoop> { profile }, floorType.Id, level.Id);
                    }
                    if (placement == FamilyPlacementType.OneLevelBasedHosted)
                    {
                        if (family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger() != 1)
                            throw new NotSupportedException("This family requires a hosted placement other than a wall.");
                        var source = new FilteredElementCollector(document).OfClass(typeof(WallType)).Cast<WallType>().First(candidate => candidate.Kind == WallKind.Basic);
                        var wallType = (WallType)source.Duplicate("Temporary family check wall " + Guid.NewGuid().ToString("N"));
                        var compound = CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure, 300 / MillimetresPerFoot, ElementId.InvalidElementId);
                        wallType.SetCompoundStructure(compound);
                        wall = Wall.Create(document, Line.CreateBound(new XYZ(-10, 0, 0), new XYZ(10, 0, 0)), wallType.Id, level.Id, 3000 / MillimetresPerFoot, 0, false, false);
                    }
                });
                if (wall is not null) beforeVolume = WallVolume(wall);
                Commit(document, () =>
                {
                    FamilyInstance? instance = null;
                    var level = temporaryLevel!;
                    if (wall is not null)
                        instance = document.Create.NewFamilyInstance(XYZ.Zero, symbol, wall, level, StructuralType.NonStructural);
                    else if (family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased)
                    {
                        var reference = HostObjectUtils.GetTopFaces(planeHost!).First();
                        var face = (Face)planeHost!.GetGeometryObjectFromReference(reference);
                        var origin = face.Project(XYZ.Zero)?.XYZPoint ?? throw new InvalidOperationException("No point on the temporary level plane.");
                        instance = document.Create.NewFamilyInstance(reference, origin, XYZ.BasisX, symbol);
                    }
                    else if (family.FamilyPlacementType is FamilyPlacementType.OneLevelBased or FamilyPlacementType.TwoLevelsBased)
                        instance = document.Create.NewFamilyInstance(XYZ.Zero, symbol, level, StructuralType.NonStructural);
                    else throw new NotSupportedException("This placement mode requires a supplied host or placement curve.");
                    if (instance is null) throw new InvalidOperationException("Revit did not create an instance.");
                });
                type.Placed = true;
                if (wall is not null && CategoryKey(document, family) is "doors" or "windows")
                {
                    type.HostCut = beforeVolume - WallVolume(wall) > 1e-9;
                    if (type.HostCut == false)
                    {
                        type.Status = "failed";
                        Add(result, "host_not_cut", type.Name, "Instance did not reduce the temporary wall volume.");
                    }
                }
            }
            catch (NotSupportedException exception)
            {
                data.Skipped.Add(new SkippedRead { What = type.Name, Reason = exception.Message });
            }
            catch (Exception exception)
            {
                type.Placed = false;
                type.Status = "failed";
                Add(result, "place_failed", type.Name, exception.Message);
            }
            finally
            {
                if (group.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("Placement rollback did not complete.");
            }
        }
    }

    private static double WallVolume(Wall wall) => wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ??
        throw new InvalidOperationException("Host volume is unavailable.");

    private static string CategoryKey(Document document, Family? family = null)
    {
        var category = (family ?? document.OwnerFamily).FamilyCategory;
        if (category is null) return "unknown";
        if (category.Id == new ElementId(BuiltInCategory.OST_Doors)) return "doors";
        if (category.Id == new ElementId(BuiltInCategory.OST_Windows)) return "windows";
        if (category.Id == new ElementId(BuiltInCategory.OST_Furniture)) return "furniture";
        if (category.Id == new ElementId(BuiltInCategory.OST_LightingFixtures)) return "lighting";
        if (category.Id == new ElementId(BuiltInCategory.OST_Casework)) return "casework";
        return "unknown";
    }

    private static bool SimplificationCommon(Document document) => CategoryKey(document) != "unknown";
    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string BoundsText(FamilyCheckBounds bounds) => $"{Number(bounds.Width)}, {Number(bounds.Depth)}, {Number(bounds.Height)}";

    private static void Add(FamilyCheckFamily family, string id, string? type, string message, string? severity = null,
        Dictionary<string, string>? evidence = null)
    {
        if (family.Checks.Any(check => check.Id == id && check.Type == type && check.Message == message && evidence is null)) return;
        family.Checks.Add(new FamilyCheckFinding { Id = id, Type = type, Severity = severity ?? FamilyCheckRules.Severity(id), Message = message, Evidence = evidence ?? [] });
    }
}
