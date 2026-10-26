// @target formula
ctx.Document.FamilyManager.SetFormula(ctx.Document.FamilyManager.get_Parameter("LegOffset"), null);
return new { summary = "Deliberately remove LegOffset formula" };
