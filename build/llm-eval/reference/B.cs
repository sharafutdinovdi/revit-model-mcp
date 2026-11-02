var document = ctx.Document;
var manager = document.FamilyManager;
document.OwnerFamily.FamilyCategory = Category.GetCategory(document, BuiltInCategory.OST_Furniture);
var width = manager.AddParameter("Width", GroupTypeId.Geometry, SpecTypeId.Length, false);
var depth = manager.AddParameter("Depth", GroupTypeId.Geometry, SpecTypeId.Length, false);
var height = manager.AddParameter("Height", GroupTypeId.Geometry, SpecTypeId.Length, false);
var offset = manager.AddParameter("LegOffset", GroupTypeId.Geometry, SpecTypeId.Length, false);
var showBrace = manager.AddParameter("ShowBrace", GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, false);
manager.NewType("Compact");
manager.Set(width, ctx.FromMm(800));
manager.Set(depth, ctx.FromMm(600));
manager.Set(height, ctx.FromMm(700));
manager.Set(showBrace, 1);
manager.SetFormula(offset, "Width / 10");
var plan = new FilteredElementCollector(document).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
    .First(view => !view.IsTemplate && Math.Abs(view.ViewDirection.Z) > 0.99);
var elevation = new FilteredElementCollector(document).OfClass(typeof(ViewSection)).Cast<ViewSection>()
    .First(view => !view.IsTemplate && Math.Abs(view.ViewDirection.Y) > 0.99);
ReferencePlane MakePlane(string name, int axis, double coordinate)
{
    var point = XYZ.Zero;
    point = axis == 0 ? new XYZ(ctx.FromMm(coordinate), 0, 0)
        : axis == 1 ? new XYZ(0, ctx.FromMm(coordinate), 0) : new XYZ(0, 0, ctx.FromMm(coordinate));
    var direction = axis == 0 ? XYZ.BasisY : XYZ.BasisX;
    var plane = document.FamilyCreate.NewReferencePlane(point - direction * 40, point + direction * 40,
        axis == 2 ? XYZ.BasisY : XYZ.BasisZ, axis == 2 ? elevation : plan);
    plane.Name = name;
    return plane;
}
var anchors = Enumerable.Range(0, 3).Select(axis => MakePlane("Anchor_" + axis, axis, -10000)).ToArray();
foreach (var anchor in anchors) anchor.Pinned = true;
Extrusion MakeBody(string name, double[] lower, double[] upper, string[] formulas)
{
    var vertices = new[] { new XYZ(ctx.FromMm(lower[0]), ctx.FromMm(lower[1]), 0),
        new XYZ(ctx.FromMm(upper[0]), ctx.FromMm(lower[1]), 0),
        new XYZ(ctx.FromMm(upper[0]), ctx.FromMm(upper[1]), 0),
        new XYZ(ctx.FromMm(lower[0]), ctx.FromMm(upper[1]), 0) };
    var profile = new CurveArray();
    for (var index = 0; index < 4; index++) profile.Append(Line.CreateBound(vertices[index], vertices[(index + 1) % 4]));
    var loops = new CurveArrArray(); loops.Append(profile);
    var sketch = SketchPlane.Create(document, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
    var body = document.FamilyCreate.NewExtrusion(true, loops, sketch, ctx.FromMm(upper[2]));
    body.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM).Set(ctx.FromMm(lower[2]));
    for (var axis = 0; axis < 3; axis++)
    for (var side = 0; side < 2; side++)
    {
        var coordinate = side == 0 ? lower[axis] : upper[axis];
        var plane = MakePlane(name + "_" + axis + "_" + side, axis, coordinate);
        var parameter = manager.AddParameter(plane.Name, GroupTypeId.Geometry, SpecTypeId.Length, false);
        manager.SetFormula(parameter, "10000 mm + (" + formulas[axis * 2 + side] + ")");
        document.Regenerate();
        var references = new ReferenceArray();
        references.Append(anchors[axis].GetReference()); references.Append(plane.GetReference());
        var direction = axis == 0 ? XYZ.BasisX : axis == 1 ? XYZ.BasisY : XYZ.BasisZ;
        var dimension = document.FamilyCreate.NewLinearDimension(axis == 2 ? elevation : plan,
            Line.CreateBound(-direction * ctx.FromMm(10000), direction * ctx.FromMm(coordinate)), references);
        dimension.FamilyLabel = parameter;
        document.Regenerate();
        var face = body.get_Geometry(new Options { ComputeReferences = true }).OfType<Solid>()
            .SelectMany(solid => solid.Faces.Cast<Face>()).OfType<PlanarFace>().Single(face =>
                Math.Abs(face.FaceNormal[axis] - (side == 0 ? -1 : 1)) < 1e-8);
        document.FamilyCreate.NewAlignment(axis == 2 ? elevation : plan, plane.GetReference(), face.Reference);
    }
    return body;
}
MakeBody("Top", new[] { -400.0, -300, 660 }, new[] { 400.0, 300, 700 },
    new[] { "-Width / 2", "Width / 2", "-Depth / 2", "Depth / 2", "Height - 40 mm", "Height" });
foreach (var signX in new[] { -1, 1 })
foreach (var signY in new[] { -1, 1 })
{
    var centerX = signX * 320.0;
    var centerY = signY * 220.0;
    var expressionX = signX + " * (Width / 2 - LegOffset)";
    var expressionY = signY + " * (Depth / 2 - LegOffset)";
    MakeBody("Leg_" + signX + "_" + signY, new[] { centerX - 25, centerY - 25, 0 },
        new[] { centerX + 25, centerY + 25, 660 },
        new[] { expressionX + " - 25 mm", expressionX + " + 25 mm", expressionY + " - 25 mm",
            expressionY + " + 25 mm", "0 mm", "Height - 40 mm" });
}
var brace = MakeBody("Brace", new[] { -320.0, -10, 590 }, new[] { 320.0, 10, 610 },
    new[] { "-Width / 2 + LegOffset", "Width / 2 - LegOffset", "-10 mm", "10 mm", "Height - 110 mm", "Height - 90 mm" });
manager.AssociateElementParameterToFamilyParameter(brace.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), showBrace);
manager.NewType("Large");
manager.Set(width, ctx.FromMm(1600)); manager.Set(depth, ctx.FromMm(900)); manager.Set(height, ctx.FromMm(760));
document.Regenerate();
manager.CurrentType = manager.Types.Cast<FamilyType>().Single(type => type.Name == "Compact");
return new { summary = "Create six solids with formula-driven planes and brace visibility" };
