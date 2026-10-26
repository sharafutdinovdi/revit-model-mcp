var document = ctx.Document;
var name = __NAME__;
var scenario = __SCENARIO__;
var typeName = scenario == "A" ? "Medium" : scenario == "B" ? "Compact" : scenario == "C" ? "Small" : "Standard";
var symbol = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
    .Single(symbol => symbol.Family.Name == name && symbol.Name == typeName);
symbol.Activate();
document.Regenerate();
var level = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().Single(level => level.Name == "Level 0");
FamilyInstance instance;
if (scenario == "C")
{
    var wall = new FilteredElementCollector(document).OfClass(typeof(Wall)).Cast<Wall>().Single();
    instance = document.Create.NewFamilyInstance(new XYZ(0, 0, ctx.FromMm(900)), symbol, wall, level,
        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
    instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM).Set(ctx.FromMm(900));
}
else
{
    instance = document.Create.NewFamilyInstance(XYZ.Zero, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
}
document.Regenerate();
if (scenario == "D")
{
    var connector = instance.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Single();
    var ductType = new FilteredElementCollector(document).OfClass(typeof(DuctType)).Cast<DuctType>()
        .First(type => type.Shape == ConnectorProfileType.Round);
    Duct.Create(document, ductType.Id, level.Id, connector, new XYZ(ctx.FromMm(1600), 0, ctx.FromMm(250)));
    document.Regenerate();
}
return new { summary = "Place reference family and connect the D duct", instanceId = instance.UniqueId };
