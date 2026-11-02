// @target rule_origin
var plane = new FilteredElementCollector(ctx.Document).OfClass(typeof(ReferencePlane))
    .Cast<ReferencePlane>().Single(plane => plane.Name == "ORG_Center_X");
plane.get_Parameter(BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN).Set(0);
return new { summary = "Deliberately disable Defines Origin on ORG_Center_X" };
