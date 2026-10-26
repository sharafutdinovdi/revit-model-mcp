var document = ctx.Document;
document.OwnerFamily.FamilyCategory = Category.GetCategory(document, BuiltInCategory.OST_MechanicalEquipment);
document.FamilyManager.NewType("Standard");
var vertices = new[] { new XYZ(0, ctx.FromMm(-200), 0), new XYZ(ctx.FromMm(600), ctx.FromMm(-200), 0),
    new XYZ(ctx.FromMm(600), ctx.FromMm(200), 0), new XYZ(0, ctx.FromMm(200), 0) };
var profile = new CurveArray();
for (var index = 0; index < 4; index++) profile.Append(Line.CreateBound(vertices[index], vertices[(index + 1) % 4]));
var loops = new CurveArrArray(); loops.Append(profile);
var sketch = SketchPlane.Create(document, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
var body = document.FamilyCreate.NewExtrusion(true, loops, sketch, ctx.FromMm(500));
document.Regenerate();
var face = body.get_Geometry(new Options { ComputeReferences = true }).OfType<Solid>()
    .SelectMany(solid => solid.Faces.Cast<Face>()).OfType<PlanarFace>()
    .Single(face => face.FaceNormal.DotProduct(XYZ.BasisX) > 0.99);
var connector = ConnectorElement.CreateDuctConnector(document, DuctSystemType.SupplyAir, ConnectorProfileType.Round, face.Reference);
connector.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER).Set(ctx.FromMm(200));
connector.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM).Set((int)FlowDirectionType.Out);
document.Regenerate();
return new { summary = "Create one Supply Air Out connector on the right face of the body", connectorId = connector.UniqueId };
