// @target types
ctx.Document.FamilyManager.CurrentType = ctx.Document.FamilyManager.Types.Cast<FamilyType>().Single(type => type.Name == "Small");
ctx.Document.FamilyManager.Set(ctx.Document.FamilyManager.get_Parameter("Width"), ctx.FromMm(1000));
return new { summary = "Deliberately change Small clear Width to 1000 mm" };
