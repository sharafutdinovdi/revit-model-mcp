"""Fixed Revit probes. These sources never come from the evaluated agent."""

from __future__ import annotations

import json
from pathlib import PureWindowsPath

FAMILY_NAMES = {
    "A": "EVAL_Box",
    "B": "EVAL_Table",
    "C": "EVAL_Window",
    "D": "EVAL_AirUnit",
    "E": "EVAL_CompanyBox",
    "A_prime": "EVAL_BoxVariant",
}


def cleanup_plan(root: str, documents: list[str], files: list[str]) -> dict:
    """Only explicit run-owned files and documents enter the cleanup plan."""
    directory = PureWindowsPath(root)
    if not directory.is_absolute() or ".." in directory.parts:
        raise ValueError("An absolute workstation output folder is required")
    paths = [PureWindowsPath(path) for path in files]
    if any(
        not path.is_relative_to(directory)
        or path == directory
        or ".." in path.parts
        or path.suffix.lower() not in {".rfa", ".rvt"}
        for path in paths
    ):
        raise ValueError("Cleanup file escapes the eval output folder")
    if len(documents) != len(set(documents)) or any(not doc for doc in documents):
        raise ValueError("Cleanup documents must have unique identities")
    return {
        "root": str(directory),
        "close": [{"document": doc, "save": False} for doc in reversed(documents)],
        "delete": [str(path) for path in paths],
    }


def parse_checks(result: dict, expected: list[str]) -> list[dict]:
    """Fail closed on missing, duplicated, malformed, or failed probe evidence."""
    payload = result.get("payload", {})
    if isinstance(payload, dict):
        payload = payload.get("data", payload)
    if isinstance(payload, dict):
        payload = payload.get("returnValue", payload)
    if isinstance(payload, str):
        try:
            payload = json.loads(payload)
        except ValueError:
            payload = {}
    rows = payload.get("checks", []) if isinstance(payload, dict) else []
    if not isinstance(rows, list):
        rows = []
    parsed = []
    for check_id in expected:
        matches = [
            row
            for row in rows
            if isinstance(row, dict) and row.get("check_id") == check_id
        ]
        if (
            not result.get("error")
            and len(matches) == 1
            and matches[0].get("status") in {"pass", "fail", "not_applicable"}
            and all(
                key in matches[0]
                for key in ("expected", "actual", "document", "ids", "reason")
            )
            and isinstance(matches[0]["reason"], str)
            and bool(matches[0]["reason"].strip())
        ):
            parsed.append(matches[0])
        else:
            parsed.append(
                {
                    "check_id": check_id,
                    "status": "fail",
                    "expected": "Complete independent API evidence",
                    "actual": None,
                    "document": None,
                    "ids": [],
                    "reason": "Missing, ambiguous, or failed probe",
                }
            )
    return parsed


def score_family(checks: list[dict], run: dict, approvals: list[dict]) -> dict:
    passed = sum(row["status"] == "pass" for row in checks)
    trace = run.get("trace", [])
    return {
        "checks_passed": passed,
        "checks_total": len(checks),
        "artifact_pass": bool(checks) and passed == len(checks),
        "seconds": run.get("seconds", 0),
        "calls": len(trace),
        "tool_errors": sum(bool(call.get("error")) for call in trace),
        "approval_challenges": sum(event["kind"] == "challenge" for event in approvals),
        "human_approvals": sum(event["kind"] == "approved" for event in approvals),
        "denials": sum(event["kind"] == "denied" for event in approvals),
        "approval_wait_seconds": sum(event.get("seconds", 0) for event in approvals),
        "agent_seconds_excluding_approvals": max(
            0,
            run.get("seconds", 0) - sum(event.get("seconds", 0) for event in approvals),
        ),
        "underlying_operations": None,
    }


# Each flex uses a SubTransaction inside execute_code's rolled-back transaction.
# Commit-time failure processing cannot be established by this dry-run contract.
FAMILY_PROBE = r"""
var document = ctx.Document;
if (document is null || !document.IsFamilyDocument)
    throw new InvalidOperationException("Expected a family document");
var manager = document.FamilyManager;
var checks = new List<object>();
void Record(string name, bool passed, string expected, object actual, IEnumerable<string> ids = null)
{
    checks.Add(new { check_id = name, status = passed ? "pass" : "fail", expected, actual,
        document = document.Title, ids = ids ?? new string[0], reason = "Independent Revit API probe" });
}
var scenario = __SCENARIO__;
var prefix = scenario == "E" ? "ORG_" : "";
var names = scenario == "D" ? new string[0] : scenario == "B"
    ? new[] { "Width", "Depth", "Height", "LegOffset", "ShowBrace" }
    : scenario == "C" ? new[] { "Width", "Height" }
    : new[] { prefix + "Width", prefix + "Depth", prefix + "Height" };
var parameters = names.Select(name => manager.get_Parameter(name)).ToArray();
Record("parameters", parameters.All(parameter => parameter != null && !parameter.IsInstance && !parameter.IsReporting
    && parameter.Definition.GetDataType() == (parameter.Definition.Name == "ShowBrace" ? SpecTypeId.Boolean.YesNo : SpecTypeId.Length)),
    "Exact type parameters, data types, and non-reporting scope", names);
var planes = new FilteredElementCollector(document).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToArray();
var dimensions = new FilteredElementCollector(document).OfClass(typeof(Dimension)).Cast<Dimension>().ToArray();
if (scenario == "A" || scenario == "A_prime" || scenario == "E")
{
    var requiredPlanes = scenario == "E" ? new[] { "ORG_Center_X", "ORG_Center_Y", "ORG_Bottom" }
        : new[] { "Left", "Right", "Front", "Back", "Bottom", "Top" };
    Record("planes", requiredPlanes.All(name => planes.Count(plane => plane.Name == name) == 1),
        "Unique named controlling reference planes", planes.Select(plane => plane.Name).ToArray(), planes.Select(plane => plane.UniqueId));
    var labeledParameters = new HashSet<ElementId>();
    var labelErrors = new List<object>();
    foreach (var dimension in dimensions)
    {
        try
        {
            var label = dimension.FamilyLabel;
            if (label != null && dimension.References.Size >= 2) labeledParameters.Add(label.Id);
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException exception)
        {
            labelErrors.Add(new { id = dimension.UniqueId, reason = exception.Message });
        }
    }
    Record("labels", parameters.All(parameter => parameter != null && labeledParameters.Contains(parameter.Id)),
        "Each controlling parameter labels a dimension; non-labelable dimensions are skipped",
        new { dimensions = dimensions.Length, skipped = labelErrors });
    var locked = new List<Dimension>();
    foreach (var dimension in dimensions)
    {
        try
        {
            if (dimension.NumberOfSegments == 0 && dimension.IsLocked && dimension.References.Size >= 2)
                locked.Add(dimension);
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException exception)
        {
            labelErrors.Add(new { id = dimension.UniqueId, reason = exception.Message });
        }
    }
    var linkedPlanes = new HashSet<ElementId>();
    var linkedForms = new HashSet<ElementId>();
    foreach (var dimension in locked)
    {
        var references = dimension.References.Cast<Reference>().ToArray();
        if (!references.Any(reference => document.GetElement(reference) is GenericForm)) continue;
        foreach (var reference in references)
        {
            if (document.GetElement(reference) is ReferencePlane) linkedPlanes.Add(reference.ElementId);
            if (document.GetElement(reference) is GenericForm) linkedForms.Add(reference.ElementId);
        }
    }
    var facePlaneNames = new[] { "Left", "Right", "Front", "Back", "Bottom", "Top" }
        .Select(name => prefix + name).ToArray();
    Record("alignments", linkedForms.Count > 0 && facePlaneNames.All(name =>
        planes.Any(plane => plane.Name == name && linkedPlanes.Contains(plane.Id))),
        "Each of the six bounding planes has a locked dimension to geometry",
        new { planes = planes.Where(plane => linkedPlanes.Contains(plane.Id)).Select(plane => plane.Name).ToArray(), forms = linkedForms.Count },
        locked.Select(dimension => dimension.UniqueId));
}
var category = document.OwnerFamily.FamilyCategory.Id;
var expectedCategory = scenario == "B" ? BuiltInCategory.OST_Furniture : scenario == "C"
    ? BuiltInCategory.OST_Windows : scenario == "D" ? BuiltInCategory.OST_MechanicalEquipment : BuiltInCategory.OST_GenericModel;
Record("category", category == new ElementId(expectedCategory), "Required built-in category", category.ToString());
var forms = new FilteredElementCollector(document).OfClass(typeof(GenericForm)).Cast<GenericForm>().Where(form => form.IsSolid).ToArray();
IEnumerable<Solid> Solids(GeometryElement geometry)
{
    foreach (var item in geometry)
    {
        if (item is Solid solid && solid.Volume > 1e-9) yield return solid;
        if (item is GeometryInstance instance)
            foreach (var nested in Solids(instance.GetInstanceGeometry())) yield return nested;
    }
}
var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
var types = manager.Types.Cast<FamilyType>().ToArray();
var typeSets = __TYPES__;
Record("types", (scenario != "D" || types.Length == 1) && typeSets.All(entry => types.Any(type => type.Name == entry.Key && entry.Value.Select((value, index) =>
    Math.Abs(ctx.ToMm(type.AsDouble(parameters[index]).Value) - value) <= 0.1).All(equal => equal))),
    "Nominal named types and exact dimensions", types.Select(type => type.Name).ToArray());
var flexSets = __FLEX__;
var flexResults = new List<object>();
var flexPassed = true;
var baselineWarnings = new HashSet<string>(document.GetWarnings().Select(warning => warning.GetFailureDefinitionId().Guid.ToString() + warning.GetDescriptionText()));
foreach (var values in flexSets)
{
    using var flex = new SubTransaction(document);
    flex.Start();
    try
    {
        manager.CurrentType = types.First();
        for (var index = 0; index < values.Length; index++) manager.Set(parameters[index], ctx.FromMm(values[index]));
        document.Regenerate();
        var solids = forms.SelectMany(form => Solids(form.get_Geometry(options))).ToArray();
        var volume = solids.Sum(solid => solid.Volume);
        var expectedVolume = values.Aggregate(1.0, (product, value) => product * ctx.FromMm(value));
        var box = scenario == "A" || scenario == "A_prime" || scenario == "E";
        var vertices = solids.SelectMany(solid => solid.Edges.Cast<Edge>().SelectMany(edge => edge.Tessellate())).ToArray();
        var boundsMatch = !box || vertices.Length > 0 && Enumerable.Range(0, 3).All(axis =>
        {
            var lower = axis == 2 ? 0 : -ctx.FromMm(values[axis]) / 2;
            var upper = axis == 2 ? ctx.FromMm(values[axis]) : ctx.FromMm(values[axis]) / 2;
            return Math.Abs(vertices.Min(point => point[axis]) - lower) < ctx.FromMm(0.1)
                && Math.Abs(vertices.Max(point => point[axis]) - upper) < ctx.FromMm(0.1);
        });
        var passed = volume > 1e-9 && boundsMatch && (!box || Math.Abs(volume / expectedVolume - 1) < 0.005);
        if (scenario == "B")
        {
            var offset = values[0] / 10;
            passed &= Math.Abs(ctx.ToMm(manager.CurrentType.AsDouble(manager.get_Parameter("LegOffset")).Value) - offset) <= 0.1;
            bool Matches(Solid solid, double[] lower, double[] upper)
            {
                var points = solid.Edges.Cast<Edge>().SelectMany(edge => edge.Tessellate()).ToArray();
                return points.Length > 0 && Enumerable.Range(0, 3).All(axis =>
                    Math.Abs(ctx.ToMm(points.Min(point => point[axis])) - lower[axis]) <= 0.1
                    && Math.Abs(ctx.ToMm(points.Max(point => point[axis])) - upper[axis]) <= 0.1);
            }
            passed &= solids.Count(solid => Matches(solid, new[] { -values[0] / 2, -values[1] / 2, values[2] - 40 },
                new[] { values[0] / 2, values[1] / 2, values[2] })) == 1;
            foreach (var directionX in new[] { -1, 1 })
            foreach (var directionY in new[] { -1, 1 })
            {
                var centerX = directionX * (values[0] / 2 - offset);
                var centerY = directionY * (values[1] / 2 - offset);
                passed &= solids.Count(solid => Matches(solid, new[] { centerX - 25, centerY - 25, 0 },
                    new[] { centerX + 25, centerY + 25, values[2] - 40 })) == 1;
            }
            passed &= solids.Count(solid => Matches(solid,
                new[] { -(values[0] / 2 - offset), -10.0, values[2] - 110 },
                new[] { values[0] / 2 - offset, 10.0, values[2] - 90 })) == 1;
        }
        if (scenario == "D")
        {
            var lower = new[] { 0.0, -200, 0 };
            var upper = new[] { 600.0, 200, 500 };
            passed &= vertices.Length > 0 && Enumerable.Range(0, 3).All(axis =>
                Math.Abs(ctx.ToMm(vertices.Min(point => point[axis])) - lower[axis]) <= 0.1
                && Math.Abs(ctx.ToMm(vertices.Max(point => point[axis])) - upper[axis]) <= 0.1);
        }
        passed &= !document.GetWarnings().Any(warning => !baselineWarnings.Contains(warning.GetFailureDefinitionId().Guid.ToString() + warning.GetDescriptionText()));
        flexPassed &= passed;
        flexResults.Add(new { values, volume, passed, warnings = document.GetWarnings().Select(warning => warning.GetFailureDefinitionId().Guid.ToString()).ToArray() });
    }
    catch (Exception exception)
    {
        flexPassed = false;
        flexResults.Add(new { values, error = exception.GetType().Name });
    }
    finally { flex.RollBack(); }
}
Record("flex", flexPassed && flexResults.Count == 3, "Three independent value sets regenerate with matching geometry", flexResults);
checks.Add(new { check_id = "flex_commit", status = "not_verified", expected = "Commit without new warnings or failures",
    actual = "dry_run rolls back the outer transaction", document = document.Title, ids = new string[0],
    reason = "Requires commit and failure-processing validation on disposable workstation copies" });
if (scenario == "B")
{
    var offset = manager.get_Parameter("LegOffset");
    Record("formula", offset != null && (offset.Formula ?? "").Replace(" ", "") == "Width/10", "LegOffset = Width / 10", offset?.Formula);
    var visibility = manager.get_Parameter("ShowBrace");
    var controlled = forms.Where(form => {
        var visible = form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
        return visible != null && manager.GetAssociatedFamilyParameter(visible)?.Id == visibility?.Id;
    }).ToArray();
    var visibilityPassed = controlled.Length == 1;
    foreach (var familyType in types)
    foreach (var value in new[] { 0, 1 })
    {
        using var toggle = new SubTransaction(document);
        toggle.Start();
        manager.CurrentType = familyType;
        manager.Set(visibility, value);
        document.Regenerate();
        visibilityPassed &= controlled.All(form => form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM).AsInteger() == value);
        visibilityPassed &= forms.Except(controlled).All(form => form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM).AsInteger() == 1);
        toggle.RollBack();
    }
    Record("visibility", visibilityPassed, "One brace Visible association follows ShowBrace; other forms stay visible", controlled.Length);
}
if (scenario == "D")
{
    var connectors = new FilteredElementCollector(document).OfClass(typeof(ConnectorElement)).ToElements();
    Record("connector_exists", connectors.Count == 1, "Exactly one family connector", connectors.Count, connectors.Select(element => element.UniqueId));
}
if (scenario == "E")
{
    Record("rule_names", manager.get_Parameter("w") == null && names.All(name => manager.get_Parameter(name) != null),
        "R3: ORG_* names, no w", names);
    Record("no_imports", !new FilteredElementCollector(document).OfClass(typeof(ImportInstance)).Any(), "R5: No CAD imports", "ImportInstance count");
    var centerX = planes.SingleOrDefault(plane => plane.Name == "ORG_Center_X");
    var centerY = planes.SingleOrDefault(plane => plane.Name == "ORG_Center_Y");
    var bottom = planes.SingleOrDefault(plane => plane.Name == "ORG_Bottom");
    Record("rule_origin", centerX != null && centerY != null && bottom != null
        && Math.Abs(centerX.GetPlane().Origin.X) <= ctx.FromMm(0.1)
        && Math.Abs(centerY.GetPlane().Origin.Y) <= ctx.FromMm(0.1)
        && Math.Abs(bottom.GetPlane().Origin.Z) <= ctx.FromMm(0.1)
        && centerX.get_Parameter(BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN).AsInteger() == 1
        && centerY.get_Parameter(BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN).AsInteger() == 1,
        "R1: Central planes at insertion origin and Defines Origin enabled", planes.Select(plane => plane.Name).ToArray());

}
return new { checks };
"""

PROJECT_PROBE = r"""
var document = ctx.Document;
var familyName = __NAME__;
var scenario = __SCENARIO__;
var instances = new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
    .Where(instance => instance.Symbol.Family.Name == familyName).ToArray();
var instanceUniqueIds = instances.Select(instance => instance.UniqueId).ToArray();
var checks = new List<object>();
void Record(string name, bool passed, string expected, object actual)
{
    checks.Add(new { check_id = name, status = passed ? "pass" : "fail", expected, actual,
        document = document.Title, ids = instanceUniqueIds, reason = "Independent project API probe" });
}
Record("placement", instances.Length == 1, "One placed result family", instances.Length);
if (scenario == "C")
{
    var instance = instances.Single();
    var instanceId = instance.Id;
    var wall = instance.Host as Wall;
    Record("host", wall != null && wall.FindInserts(true, true, true, true).Contains(instance.Id), "Window hosted by fixture wall", wall?.UniqueId);
    var before = wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED).AsDouble();
    var width = instance.Symbol.LookupParameter("Width").AsDouble();
    var height = instance.Symbol.LookupParameter("Height").AsDouble();
    using var removal = new SubTransaction(document);
    removal.Start();
    document.Delete(instance.Id);
    document.Regenerate();
    var restored = wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED).AsDouble();
    removal.RollBack();
    instance = (FamilyInstance)document.GetElement(instanceId);
    Record("opening_cuts_host", Math.Abs((restored - before) / (width * height * wall.Width) - 1) < 0.005,
        "Wall volume restored by Width * Height * thickness after instance removal", restored - before);
    Record("no_project_opening", !new FilteredElementCollector(document).OfClass(typeof(Opening)).Any(), "No separate project Opening", "Opening count");
    var symbols = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
        .Where(symbol => symbol.Family.Name == familyName && new[] { "Small", "Large" }.Contains(symbol.Name)).ToArray();
    var wallTypes = new FilteredElementCollector(document).OfClass(typeof(WallType)).Cast<WallType>()
        .Where(type => Math.Abs(ctx.ToMm(type.Width) - 150) <= 0.1 || Math.Abs(ctx.ToMm(type.Width) - 300) <= 0.1).ToArray();
    var variants = new List<object>();
    var matrixPassed = symbols.Length == 2 && wallTypes.Select(type => Math.Round(ctx.ToMm(type.Width))).Distinct().Count() == 2;
    foreach (var symbol in symbols)
    foreach (var wallType in wallTypes)
    foreach (var flipped in new[] { false, true })
    {
        using var variant = new SubTransaction(document);
        variant.Start();
        try
        {
            wall.ChangeTypeId(wallType.Id);
            instance.Symbol = symbol;
            if (instance.FacingFlipped != flipped) instance.flipFacing();
            document.Regenerate();
            var openingWidth = symbol.LookupParameter("Width").AsDouble();
            var openingHeight = symbol.LookupParameter("Height").AsDouble();
            var cutVolume = wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED).AsDouble();
            var passed = instance.Host.Id == wall.Id && instance.FacingFlipped == flipped;
            var wallSolids = wall.get_Geometry(new Options()).OfType<Solid>().Where(solid => solid.Volume > 1e-9).ToArray();
            var transform = instance.GetTransform();
            var direction = instance.FacingOrientation.Normalize();
            var rayLength = wall.Width + ctx.FromMm(100);
            foreach (var horizontal in new[] { -0.45, 0.0, 0.45 })
            foreach (var vertical in new[] { 0.1, 0.5, 0.9 })
            {
                var point = transform.OfPoint(new XYZ(horizontal * openingWidth, 0, vertical * openingHeight));
                var ray = Line.CreateBound(point - direction * rayLength, point + direction * rayLength);
                using var intersectionOptions = new SolidCurveIntersectionOptions();
                passed &= wallSolids.All(solid => solid.IntersectWithCurve(ray, intersectionOptions).SegmentCount == 0);
            }
            foreach (var side in new[] { -1, 1 })
            {
                var point = transform.OfPoint(new XYZ(side * (openingWidth / 2 + ctx.FromMm(100)), 0, openingHeight / 2));
                var ray = Line.CreateBound(point - direction * rayLength, point + direction * rayLength);
                using var intersectionOptions = new SolidCurveIntersectionOptions();
                passed &= wallSolids.Any(solid => solid.IntersectWithCurve(ray, intersectionOptions).SegmentCount > 0);
            }
            document.Delete(instance.Id);
            document.Regenerate();
            var restoredVolume = wall.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED).AsDouble();
            passed &= Math.Abs((restoredVolume - cutVolume) / (openingWidth * openingHeight * wall.Width) - 1) < 0.005;
            matrixPassed &= passed;
            variants.Add(new { type = symbol.Name, thickness = ctx.ToMm(wallType.Width), flipped, passed });
        }
        catch (Exception exception)
        {
            matrixPassed = false;
            variants.Add(new { type = symbol.Name, flipped, error = exception.GetType().Name });
        }
        finally { variant.RollBack(); instance = (FamilyInstance)document.GetElement(instanceId); }
    }
    Record("opening_matrix", matrixPassed && variants.Count >= 8,
        "Two types, 150/300 mm walls, both facing states; clear interior rays, intact exterior, and restored volume", variants);

}
if (scenario == "D")
{
    var instance = instances.Single();
    var connectors = instance.MEPModel.ConnectorManager.Connectors.Cast<Connector>()
        .Where(connector => connector.Domain == Domain.DomainHvac && connector.ConnectorType == ConnectorType.End).ToArray();
    var connector = connectors.Single();
    Record("connector_properties", connectors.Length == 1 && connector.Shape == ConnectorProfileType.Round
        && Math.Abs(ctx.ToMm(connector.Radius) - 100) <= 0.1 && connector.DuctSystemType == DuctSystemType.SupplyAir
        && connector.AssignedFlowDirection == FlowDirectionType.Out
        && (connector.Direction == FlowDirectionType.Out || connector.Direction == FlowDirectionType.Bidirectional)
        && connector.Origin.DistanceTo(new XYZ(ctx.FromMm(600), 0, ctx.FromMm(250))) <= ctx.FromMm(0.1)
        && connector.CoordinateSystem.BasisZ.AngleTo(XYZ.BasisX) <= Math.PI / 1800,
        "Round 200 mm Supply Air connector, assigned Out, final Out or Bidirectional, at (600,0,250), +X",
        new { count = connectors.Length, assignedDirection = connector.AssignedFlowDirection.ToString(),
            direction = connector.Direction.ToString(), diameter = ctx.ToMm(connector.Radius) * 2,
            classification = connector.DuctSystemType.ToString(), origin = connector.Origin, axis = connector.CoordinateSystem.BasisZ });
    var ducts = new FilteredElementCollector(document).OfClass(typeof(Duct)).Cast<Duct>().ToArray();
    var ductGeometryPassed = ducts.Length == 1;
    foreach (var duct in ducts)
    {
        var curve = ((LocationCurve)duct.Location).Curve;
        var start = new XYZ(ctx.FromMm(600), 0, ctx.FromMm(250));
        var end = new XYZ(ctx.FromMm(1600), 0, ctx.FromMm(250));
        ductGeometryPassed &= Math.Abs(ctx.ToMm(curve.Length) - 1000) <= 0.1
            && Math.Abs(ctx.ToMm(duct.Diameter) - 200) <= 0.1
            && (curve.GetEndPoint(0).DistanceTo(start) <= ctx.FromMm(0.1) && curve.GetEndPoint(1).DistanceTo(end) <= ctx.FromMm(0.1)
                || curve.GetEndPoint(1).DistanceTo(start) <= ctx.FromMm(0.1) && curve.GetEndPoint(0).DistanceTo(end) <= ctx.FromMm(0.1))
            && duct.MEPSystem is MechanicalSystem ductSystem && ductSystem.SystemType == DuctSystemType.SupplyAir;
    }
    Record("duct_geometry", ductGeometryPassed, "One 1000 mm Supply Air duct, diameter 200 mm, exact endpoints", ducts.Length);
    var peers = connector.AllRefs.Cast<Connector>().Where(peer => peer.Owner is Duct && peer.ConnectorType == ConnectorType.End).ToArray();
    Record("connector_connected", connector.IsConnected && peers.Length == 1 && peers.All(peer => peer.IsConnectedTo(connector)
        && peer.AllRefs.Cast<Connector>().Any(back => back.Owner.Id == instance.Id)
        && peer.Origin.DistanceTo(connector.Origin) <= ctx.FromMm(0.1)
        && Math.Abs(peer.CoordinateSystem.BasisZ.AngleTo(connector.CoordinateSystem.BasisZ) - Math.PI) <= Math.PI / 1800),
        "Reciprocal physical connection to created Duct", peers.Select(peer => peer.Owner.UniqueId).ToArray());
    var system = connector.MEPSystem;
    Record("system_membership", system != null && system.Elements.Cast<Element>().Any(element => element.Id == instance.Id)
        && peers.Length == 1 && system is MechanicalSystem mechanical && mechanical.SystemType == DuctSystemType.SupplyAir
        && peers.All(peer => mechanical.DuctNetwork.Cast<Element>().Any(element => element.Id == peer.Owner.Id)),
        "Supply Air system contains equipment and duct", system?.UniqueId);
}
return new { checks };
"""


def probe_code(scenario: str, *, project: bool = False) -> str:
    code = PROJECT_PROBE if project else FAMILY_PROBE
    nominal = {
        "A": {
            "Small": [400, 300, 200],
            "Medium": [800, 600, 500],
            "Large": [1200, 900, 1000],
        },
        "A_prime": {
            "Small": [500, 350, 250],
            "Medium": [1000, 700, 650],
            "Large": [1400, 1000, 1100],
        },
        "B": {"Compact": [800, 600, 700], "Large": [1600, 900, 760]},
        "C": {"Small": [900, 1200], "Large": [1500, 1500]},
        "D": {},
        "E": {"Standard": [800, 600, 500]},
    }[scenario]
    sets = [[950, 600, 500], [800, 450, 500], [800, 600, 750]]
    if scenario == "A_prime":
        sets = [[1150, 700, 650], [1000, 550, 650], [1000, 700, 850]]
    elif scenario == "B":
        sets = [[800, 600, 700], [1600, 900, 760], [1200, 750, 740]]
    elif scenario == "C":
        sets = [[900, 1200], [1500, 1500], [1100, 1350]]
    elif scenario == "D":
        sets = [[], [], []]
    elif scenario == "E":
        sets = [[800, 600, 500], [600, 400, 300], [1200, 900, 1000]]
    types = (
        "new Dictionary<string, double[]> {"
        + ",".join(
            "{"
            + json.dumps(name)
            + ", new double[] {"
            + ",".join(map(str, values))
            + "}}"
            for name, values in nominal.items()
        )
        + "}"
    )
    flex = (
        "new double[][] {"
        + ",".join(
            "new double[] {" + ",".join(map(str, values)) + "}" for values in sets
        )
        + "}"
    )
    return (
        code.replace("__SCENARIO__", json.dumps(scenario))
        .replace("__NAME__", json.dumps(FAMILY_NAMES[scenario]))
        .replace("__TYPES__", types)
        .replace("__FLEX__", flex)
    )


def expected_checks(scenario: str, *, project: bool = False) -> list[str]:
    if project:
        return ["placement"] + (
            {
                "C": [
                    "host",
                    "opening_cuts_host",
                    "no_project_opening",
                    "opening_matrix",
                ],
                "D": [
                    "connector_properties",
                    "duct_geometry",
                    "connector_connected",
                    "system_membership",
                ],
            }.get(scenario, [])
        )
    checks = ["parameters", "category", "types", "flex"]
    if scenario in {"A", "A_prime", "E"}:
        checks += ["planes", "labels", "alignments"]
    if scenario == "B":
        checks += ["formula", "visibility"]
    if scenario == "D":
        checks += ["connector_exists"]
    if scenario == "E":
        checks += ["rule_names", "no_imports", "rule_origin", "rule_violations"]
    return checks


def rule_report(answer: str, manifest: dict, document: str) -> dict:
    try:
        report = json.loads(answer[answer.index("{") : answer.rindex("}") + 1])
        before = report["before_violations"]
        defects = {row["defect_id"]: row for row in before}
        valid = len(before) == 3 and set(defects) == {
            "origin",
            "plane_names",
            "parameter_name",
        }
        for name, rule in (
            ("origin", "R1"),
            ("plane_names", "R2"),
            ("parameter_name", "R3"),
        ):
            valid &= defects[name]["rule_id"] == rule
        valid &= set(defects["plane_names"]["element_unique_ids"]) == set(
            manifest["plane_ids"]
        )
        valid &= defects["parameter_name"]["parameter_name"] == "w"
        valid &= bool(defects["origin"].get("element_unique_ids")) and set(
            defects["origin"]["element_unique_ids"]
        ) <= set(manifest["origin_ids"])
        valid &= report["after_violations"] == []
    except (ValueError, KeyError, TypeError):
        valid = False
    return {
        "check_id": "rule_violations",
        "status": "pass" if valid else "fail",
        "expected": "Exactly the seeded R1/R2/R3 violations with matching object identities; no remaining violations",
        "actual": valid,
        "document": document,
        "ids": manifest.get("plane_ids", []),
        "reason": "Agent JSON compared with independently generated fixture manifest",
    }


def fixture_code() -> str:
    """Create E in an empty Generic Model template, with stable defect identities."""
    return r"""
var document = ctx.Document;
if (!document.IsFamilyDocument) throw new InvalidOperationException("Expected family fixture");
var manager = document.FamilyManager;
var view = new FilteredElementCollector(document).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(candidate => !candidate.IsTemplate && Math.Abs(candidate.ViewDirection.Z) > 0.99);
var elevation = new FilteredElementCollector(document).OfClass(typeof(ViewSection)).Cast<ViewSection>().First(candidate => !candidate.IsTemplate && Math.Abs(candidate.ViewDirection.Y) > 0.99);
var width = manager.AddParameter("w", GroupTypeId.Geometry, SpecTypeId.Length, false);
var depth = manager.AddParameter("ORG_Depth", GroupTypeId.Geometry, SpecTypeId.Length, false);
var height = manager.AddParameter("ORG_Height", GroupTypeId.Geometry, SpecTypeId.Length, false);
manager.NewType("Standard");
manager.Set(width, ctx.FromMm(800));
manager.Set(depth, ctx.FromMm(600));
manager.Set(height, ctx.FromMm(500));
ReferencePlane Vertical(string name, double coordinate, bool alongX)
{
    var start = alongX ? new XYZ(ctx.FromMm(coordinate), -2, 0) : new XYZ(-2, ctx.FromMm(coordinate), 0);
    var end = alongX ? new XYZ(ctx.FromMm(coordinate), 2, 0) : new XYZ(2, ctx.FromMm(coordinate), 0);
    var plane = document.FamilyCreate.NewReferencePlane(start, end, XYZ.BasisZ, view);
    plane.Name = name;
    return plane;
}
var left = Vertical("ORG_Left", -300, true);
var centerX = Vertical("Reference Plane 1", 100, true);
var right = Vertical("ORG_Right", 500, true);
var front = Vertical("ORG_Front", -300, false);
var centerY = Vertical("Reference Plane 2", 0, false);
var back = Vertical("ORG_Back", 300, false);
ReferencePlane Horizontal(string name, double coordinate)
{
    var plane = document.FamilyCreate.NewReferencePlane(new XYZ(-2, 0, ctx.FromMm(coordinate)),
        new XYZ(2, 0, ctx.FromMm(coordinate)), XYZ.BasisY, elevation);
    plane.Name = name;
    return plane;
}
var bottom = Horizontal("ORG_Bottom", 0);
var top = Horizontal("ORG_Top", 500);
document.Regenerate();
void Label(ReferencePlane first, ReferencePlane last, FamilyParameter parameter, View target, XYZ start, XYZ end)
{
    var references = new ReferenceArray();
    references.Append(first.GetReference());
    references.Append(last.GetReference());
    document.FamilyCreate.NewLinearDimension(target, Line.CreateBound(start, end), references).FamilyLabel = parameter;
}
Label(left, right, width, view, new XYZ(-2, -3, 0), new XYZ(2, -3, 0));
Label(front, back, depth, view, new XYZ(-3, -2, 0), new XYZ(-3, 2, 0));
Label(bottom, top, height, elevation, new XYZ(-3, 0, 0), new XYZ(-3, 0, 2));
void Equal(ReferencePlane first, ReferencePlane center, ReferencePlane last, XYZ start, XYZ end)
{
    var references = new ReferenceArray();
    references.Append(first.GetReference()); references.Append(center.GetReference()); references.Append(last.GetReference());
    document.FamilyCreate.NewLinearDimension(view, Line.CreateBound(start, end), references).AreSegmentsEqual = true;
}
Equal(left, centerX, right, new XYZ(-2, -4, 0), new XYZ(2, -4, 0));
Equal(front, centerY, back, new XYZ(-4, -2, 0), new XYZ(-4, 2, 0));
var vertices = new[] { new XYZ(ctx.FromMm(-300), ctx.FromMm(-300), 0), new XYZ(ctx.FromMm(500), ctx.FromMm(-300), 0),
    new XYZ(ctx.FromMm(500), ctx.FromMm(300), 0), new XYZ(ctx.FromMm(-300), ctx.FromMm(300), 0) };
var profile = new CurveArray();
for (var index = 0; index < vertices.Length; index++) profile.Append(Line.CreateBound(vertices[index], vertices[(index + 1) % vertices.Length]));
var loops = new CurveArrArray(); loops.Append(profile);
var sketch = SketchPlane.Create(document, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
var extrusion = document.FamilyCreate.NewExtrusion(true, loops, sketch, ctx.FromMm(500));
document.Regenerate();
var options = new Options { ComputeReferences = true };
var solids = extrusion.get_Geometry(options).OfType<Solid>().Where(solid => solid.Volume > 0).ToArray();
var planes = new[] { left, right, front, back, bottom, top };
foreach (var plane in planes)
{
    var face = solids.SelectMany(solid => solid.Faces.Cast<Face>()).OfType<PlanarFace>().Single(face =>
        Math.Abs(Math.Abs(face.FaceNormal.DotProduct(plane.Normal)) - 1) < 1e-8
        && Math.Abs((face.Origin - plane.GetPlane().Origin).DotProduct(plane.Normal)) < 1e-8);
    document.FamilyCreate.NewAlignment(plane == bottom || plane == top ? elevation : view, plane.GetReference(), face.Reference);
}
foreach (var plane in new[] { bottom, centerX, centerY }) plane.Pinned = true;
document.Regenerate();
return new { plane_ids = new[] { centerX.UniqueId, centerY.UniqueId }, origin_ids = new[] { centerX.UniqueId, extrusion.UniqueId },
    parameter_id = width.Id.ToString(), form_ids = new[] { extrusion.UniqueId },
    defect_ids = new[] { "origin", "plane_names", "parameter_name" } };
"""


def identity_code(manifest: dict, source: str, source_hash: str) -> str:
    parameter_id = json.dumps(manifest.get("parameter_id", ""))
    form_ids = ",".join(json.dumps(value) for value in manifest.get("form_ids", []))
    plane_ids = ",".join(json.dumps(value) for value in manifest.get("plane_ids", []))
    return f"""
var document = ctx.Document;
var manager = document.FamilyManager;
var checks = new List<object>();
void Record(string name, bool passed, string expected, object actual)
{{
    checks.Add(new {{ check_id = name, status = passed ? "pass" : "fail", expected, actual,
        document = document.Title, ids = new string[0], reason = "Seeded fixture identity and source hash" }});
}}
var parameter = manager.get_Parameter("ORG_Width");
var formIds = new string[] {{ {form_ids} }};
var planeIds = new string[] {{ {plane_ids} }};
var currentForms = new FilteredElementCollector(document).OfClass(typeof(GenericForm)).Cast<GenericForm>().Where(form => form.IsSolid).ToArray();
Record("rule_identity", parameter != null && parameter.Id.ToString() == {parameter_id}
    && formIds.Length == currentForms.Length && formIds.All(id => document.GetElement(id) is GenericForm)
    && planeIds.Length == 2 && planeIds.All(id => document.GetElement(id) is ReferencePlane),
    "R4/R5: Geometry, renamed Width, and central plane identities survive repair", currentForms.Length);
using var algorithm = System.Security.Cryptography.SHA256.Create();
var hash = BitConverter.ToString(algorithm.ComputeHash(System.IO.File.ReadAllBytes({json.dumps(source)}))).Replace("-", "").ToLowerInvariant();
Record("source_unchanged", hash == {json.dumps(source_hash)}, "R5: Source RFA hash unchanged", hash);
return new {{ checks }};
"""
