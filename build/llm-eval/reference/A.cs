var document = ctx.Document;
if (!document.IsFamilyDocument) throw new InvalidOperationException("Expected family fixture");
var manager = document.FamilyManager;
var view = new FilteredElementCollector(document).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(candidate => !candidate.IsTemplate && Math.Abs(candidate.ViewDirection.Z) > 0.99);
var elevation = new FilteredElementCollector(document).OfClass(typeof(ViewSection)).Cast<ViewSection>().First(candidate => !candidate.IsTemplate && Math.Abs(candidate.ViewDirection.Y) > 0.99);
var width = manager.AddParameter("Width", GroupTypeId.Geometry, SpecTypeId.Length, false);
var depth = manager.AddParameter("Depth", GroupTypeId.Geometry, SpecTypeId.Length, false);
var height = manager.AddParameter("Height", GroupTypeId.Geometry, SpecTypeId.Length, false);
manager.NewType("Medium");
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
var left = Vertical("Left", -400, true);
var centerX = Vertical("Center_X", 0, true);
var right = Vertical("Right", 400, true);
var front = Vertical("Front", -300, false);
var centerY = Vertical("Center_Y", 0, false);
var back = Vertical("Back", 300, false);
ReferencePlane Horizontal(string name, double coordinate)
{
    var plane = document.FamilyCreate.NewReferencePlane(new XYZ(-2, 0, ctx.FromMm(coordinate)),
        new XYZ(2, 0, ctx.FromMm(coordinate)), XYZ.BasisY, elevation);
    plane.Name = name;
    return plane;
}
var bottom = Horizontal("Bottom", 0);
var top = Horizontal("Top", 500);
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
var vertices = new[] { new XYZ(ctx.FromMm(-400), ctx.FromMm(-300), 0), new XYZ(ctx.FromMm(400), ctx.FromMm(-300), 0),
    new XYZ(ctx.FromMm(400), ctx.FromMm(300), 0), new XYZ(ctx.FromMm(-400), ctx.FromMm(300), 0) };
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
foreach (var values in new[] { new[] { 400.0, 300, 200 }, new[] { 1200.0, 900, 1000 } })
{
    manager.NewType(values[0] == 400 ? "Small" : "Large");
    manager.Set(width, ctx.FromMm(values[0]));
    manager.Set(depth, ctx.FromMm(values[1]));
    manager.Set(height, ctx.FromMm(values[2]));
    document.Regenerate();
}
manager.CurrentType = manager.Types.Cast<FamilyType>().Single(type => type.Name == "Medium");
return new { summary = "Create centered parametric box with labeled and locked references" };
