var document = ctx.Document;
var manager = document.FamilyManager;
var width = manager.get_Parameter("Width");
var height = manager.get_Parameter("Height");
manager.NewType("Small"); manager.Set(width, ctx.FromMm(900)); manager.Set(height, ctx.FromMm(1200));
manager.NewType("Large"); manager.Set(width, ctx.FromMm(1500)); manager.Set(height, ctx.FromMm(1500));
foreach (var templateType in manager.Types.Cast<FamilyType>().Where(type => type.Name != "Small" && type.Name != "Large").ToArray())
{
    manager.CurrentType = templateType;
    manager.DeleteCurrentType();
}
foreach (FamilyType familyType in manager.Types)
{
    manager.CurrentType = familyType;
    manager.Set(manager.get_Parameter("Default Sill Height"), ctx.FromMm(800));
}
manager.CurrentType = manager.Types.Cast<FamilyType>().Single(type => type.Name == "Large");
document.Regenerate();
var sketch = SketchPlane.Create(document, Plane.CreateByNormalAndOrigin(-XYZ.BasisY, XYZ.Zero));
var localSill = ctx.FromMm(800);
var loops = new CurveArrArray();
foreach (var offset in new[] { ctx.FromMm(50), 0 })
{
    var vertices = new[] { new XYZ(-ctx.FromMm(750) - offset, 0, localSill - offset),
        new XYZ(ctx.FromMm(750) + offset, 0, localSill - offset), new XYZ(ctx.FromMm(750) + offset, 0, localSill + ctx.FromMm(1500) + offset),
        new XYZ(-ctx.FromMm(750) - offset, 0, localSill + ctx.FromMm(1500) + offset) };
    var profile = new CurveArray();
    for (var index = 0; index < 4; index++) profile.Append(Line.CreateBound(vertices[index], vertices[(index + 1) % 4]));
    loops.Append(profile);
}
var frame = document.FamilyCreate.NewExtrusion(true, loops, sketch, ctx.FromMm(50));
frame.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM).Set(-ctx.FromMm(50));
document.Regenerate();
var curves = frame.Sketch.Profile.Cast<CurveArray>().SelectMany(profile => profile.Cast<Curve>()).ToArray();
var view = new FilteredElementCollector(document).OfClass(typeof(ViewSection)).Cast<ViewSection>()
    .First(view => !view.IsTemplate && Math.Abs(view.ViewDirection.Y) > 0.99);
var planes = new FilteredElementCollector(document).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToArray();
var names = new[] { "Right", "Head", "Left", "Sill" };
for (var index = 0; index < 4; index++)
{
    var plane = planes.Single(plane => plane.Name == names[index]);
    document.FamilyCreate.NewAlignment(view, plane.GetReference(), curves[4 + index].Reference);
    var references = new ReferenceArray(); references.Append(curves[index].Reference); references.Append(curves[4 + index].Reference);
    var midpoint = (curves[4 + index].GetEndPoint(0) + curves[4 + index].GetEndPoint(1)) * 0.5;
    var direction = index % 2 == 0 ? XYZ.BasisX : XYZ.BasisZ;
    document.FamilyCreate.NewLinearDimension(view, Line.CreateBound(midpoint - direction * 2, midpoint + direction * 2), references).IsLocked = true;
}
foreach (var familyType in manager.Types.Cast<FamilyType>())
{
    manager.CurrentType = familyType;
    document.Regenerate();
}
manager.CurrentType = manager.Types.Cast<FamilyType>().Single(type => type.Name == "Small");
return new { summary = "Use native parametric opening and constrain a 50 mm frame to its planes" };
