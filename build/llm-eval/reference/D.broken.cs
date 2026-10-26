// @target connector_connected
// @project
var instance = new FilteredElementCollector(ctx.Document).OfClass(typeof(FamilyInstance))
    .Cast<FamilyInstance>().Single(instance => instance.Symbol.Family.Name == "EVAL_AirUnit");
var connector = instance.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Single();
foreach (var peer in connector.AllRefs.Cast<Connector>().Where(peer => peer.Owner is Duct).ToArray())
    connector.DisconnectFrom(peer);
return new { summary = "Deliberately disconnect the duct from equipment" };
