// @target alignments
var document = ctx.Document;
var left = new FilteredElementCollector(document).OfClass(typeof(ReferencePlane))
    .Cast<ReferencePlane>().Single(plane => plane.Name == "Left");
var alignment = new FilteredElementCollector(document).OfClass(typeof(Dimension)).Cast<Dimension>()
    .Single(dimension => dimension.NumberOfSegments == 0 && dimension.IsLocked
        && dimension.References.Cast<Reference>().Any(reference => reference.ElementId == left.Id)
        && dimension.References.Cast<Reference>().Any(reference => document.GetElement(reference) is GenericForm));
alignment.IsLocked = false;
return new { summary = "Deliberately unlock the Left face alignment" };
