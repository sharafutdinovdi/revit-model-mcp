using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class ActionMutations
{
    internal static ActionResultData PlaceFamily(Document document, ActionJobContract action)
    {
        using var symbols = document.CollectElements().OfClass<FamilySymbol>()
            .WhereParameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME).Equals(action.Family!);
        if (!symbols.Any())
        {
            using var familyCollector = document.CollectElements().OfClass<Family>();
            var families = familyCollector.Cast<Family>().ToList();
            var categories = families.GroupBy(loaded => loaded.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => string.Join(", ", group.Select(loaded => loaded.FamilyCategory?.Name ?? "Uncategorized").Distinct()),
                    StringComparer.OrdinalIgnoreCase);
            var suggestions = ActionJobParser.ClosestFamilyNames(action.Family!, categories.Keys)
                .Select(name => $"{name} ({categories[name]})").ToList();
            throw new FamilyNotLoadedException(action.Family!, suggestions);
        }
        if (action.TypeName is not null)
            symbols.WhereParameter(BuiltInParameter.SYMBOL_NAME_PARAM).Equals(action.TypeName);
        var symbol = symbols.FirstOrDefault() as FamilySymbol
                     ?? throw new ArgumentException($"Type '{action.TypeName}' was not found in family '{action.Family}'.");
        var level = FindLevel(document, action.Level!);
        if (!symbol.IsActive)
        {
            symbol.Activate();
            document.Regenerate();
        }
        var point = new XYZ(Millimeters(action.XMm), Millimeters(action.YMm), level.ProjectElevation);
        var instance = document.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
        if (action.RotationDeg != 0)
            instance.Rotate(Line.CreateBound(point, point + XYZ.BasisZ), action.RotationDeg * Math.PI / 180);
        return new ActionResultData { Id = RevitValueReader.GetId(instance.Id), Category = instance.Category?.Name, Level = level.Name };
    }

    internal static ActionResultData CreateWall(Document document, ActionJobContract action)
    {
        var level = FindLevel(document, action.Level!);
        using var types = document.CollectElements().OfClass<WallType>();
        if (action.WallType is not null)
            types.WhereParameter(BuiltInParameter.SYMBOL_NAME_PARAM).Equals(action.WallType);
        var wallType = types.Cast<WallType>().FirstOrDefault(candidate => action.WallType is not null || candidate.Kind == WallKind.Basic)
                       ?? throw new ArgumentException($"Wall type '{action.WallType ?? "basic wall"}' was not found.");
        var start = new XYZ(Millimeters(action.StartMm[0]), Millimeters(action.StartMm[1]), level.ProjectElevation);
        var end = new XYZ(Millimeters(action.EndMm[0]), Millimeters(action.EndMm[1]), level.ProjectElevation);
        var line = Line.CreateBound(start, end);
        var wall = Wall.Create(document, line, wallType.Id, level.Id, Millimeters(action.HeightMm), 0, false, false);
        return new ActionResultData { Id = RevitValueReader.GetId(wall.Id), LengthMm = line.Length.ToMillimeters() };
    }

    internal static ActionResultData CreateView(Document document, ActionJobContract action)
    {
        var family = action.Kind switch
        {
            "floor_plan" => ViewFamily.FloorPlan,
            "ceiling_plan" => ViewFamily.CeilingPlan,
            "structural_plan" => ViewFamily.StructuralPlan,
            "section" => ViewFamily.Section,
            "3d" => ViewFamily.ThreeDimensional,
            _ => ViewFamily.Drafting
        };
        using var types = document.CollectElements().OfClass<ViewFamilyType>();
        var type = types.Cast<ViewFamilyType>().FirstOrDefault(candidate => candidate.ViewFamily == family &&
            (action.ViewFamilyType is null || string.Equals(candidate.Name, action.ViewFamilyType, StringComparison.OrdinalIgnoreCase)))
            ?? throw new ArgumentException($"View family type '{action.ViewFamilyType ?? family.ToString()}' was not found.");
        View view;
        if (action.Kind is "floor_plan" or "ceiling_plan" or "structural_plan")
            view = ViewPlan.Create(document, type.Id, FindLevel(document, action.Level!).Id);
        else if (action.Kind == "drafting")
            view = ViewDrafting.Create(document, type.Id);
        else if (action.Kind == "3d")
        {
            var created = View3D.CreateIsometric(document, type.Id);
            created.SetSectionBox(ResolveBox(document, action));
            created.IsSectionBoxActive = true;
            view = created;
        }
        else
        {
            var bounds = ResolveBox(document, action);
            var minimum = bounds.Min;
            var maximum = bounds.Max;
            var center = (minimum + maximum) / 2;
            var transform = Transform.Identity;
            transform.Origin = center;
            transform.BasisX = XYZ.BasisX;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = -XYZ.BasisY;
            var sectionBox = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-(maximum.X - minimum.X) / 2, -(maximum.Z - minimum.Z) / 2, -(maximum.Y - minimum.Y) / 2),
                Max = new XYZ((maximum.X - minimum.X) / 2, (maximum.Z - minimum.Z) / 2, (maximum.Y - minimum.Y) / 2)
            };
            view = ViewSection.CreateSection(document, type.Id, sectionBox);
        }
        SetViewName(document, view, action.Name);
        if (action.Scale is int scale) view.Scale = scale;
        if (action.Template is not null) ApplyTemplate(document, view, action.Template);
        return new ActionResultData { Id = RevitValueReader.GetId(view.Id), Category = "View", Names = [view.Name], Count = 1 };
    }

    internal static ActionResultData DuplicateView(Document document, ActionJobContract action)
    {
        var source = FindView(document, action.View!);
        var option = action.Mode switch
        {
            "with_detailing" => ViewDuplicateOption.WithDetailing,
            "dependent" => ViewDuplicateOption.AsDependent,
            _ => ViewDuplicateOption.Duplicate
        };
        if (!source.CanViewBeDuplicated(option)) throw new ArgumentException($"View '{source.Name}' cannot be duplicated in this mode.");
        var duplicate = document.GetElement(source.Duplicate(option)) as View
            ?? throw new InvalidOperationException("Duplicated view was not found.");
        SetViewName(document, duplicate, action.Name);
        return new ActionResultData { Id = RevitValueReader.GetId(duplicate.Id), Category = "View", Names = [duplicate.Name], Count = 1 };
    }

    internal static ActionResultData ApplyViewTemplate(Document document, ActionJobContract action)
    {
        var names = new List<string>();
        var mismatches = new List<string>();
        var changed = new List<long>();
        foreach (var reference in action.Views!)
        {
            var view = FindView(document, reference);
            if (!TemplateMatches(document, view, action.Template!))
            {
                mismatches.Add(view.Name);
                continue;
            }
            ApplyTemplate(document, view, action.Template!);
            names.Add(view.Name);
            changed.Add(RevitValueReader.GetId(view.Id));
        }
        return new ActionResultData { Count = names.Count, Names = names, TypeMismatches = mismatches,
            Verification = new ActionVerification { Changed = changed } };
    }

    internal static ActionResultData CreateSheet(Document document, ActionJobContract action)
    {
        using var sheets = document.CollectElements().OfClass<ViewSheet>();
        if (sheets.Cast<ViewSheet>().Any(sheet => string.Equals(sheet.SheetNumber, action.Number, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Sheet number '{action.Number}' already exists.");
        using var blocks = document.CollectElements().OfClass<FamilySymbol>().OfCategory(BuiltInCategory.OST_TitleBlocks);
        var titleBlock = blocks.Cast<FamilySymbol>().FirstOrDefault(symbol => action.TitleBlock is null ||
            string.Equals(symbol.FamilyName, action.TitleBlock, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{symbol.FamilyName}: {symbol.Name}", action.TitleBlock, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Title block '{action.TitleBlock ?? "loaded"}' was not found.");
        var sheet = ViewSheet.Create(document, titleBlock.Id);
        sheet.SheetNumber = action.Number!;
        sheet.Name = action.Name!;
        return new ActionResultData { Id = RevitValueReader.GetId(sheet.Id), Category = "Sheet", Names = [sheet.SheetNumber], Count = 1 };
    }

    internal static ActionResultData PlaceViewsOnSheet(Document document, ActionJobContract action)
    {
        using var sheets = document.CollectElements().OfClass<ViewSheet>();
        var sheet = sheets.Cast<ViewSheet>().FirstOrDefault(candidate =>
            string.Equals(candidate.SheetNumber, action.Sheet, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name, action.Sheet, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Sheet '{action.Sheet}' was not found.");
        var outline = sheet.Outline;
        var gap = Millimeters(20);
        var cursorX = outline.Min.U + gap;
        var cursorY = outline.Max.V - gap;
        var rowHeight = 0.0;
        var names = new List<string>();
        foreach (var placement in action.Placements!)
        {
            var view = FindView(document, placement.View);
            if (view is ViewSchedule schedule)
            {
                if (schedule.IsTitleblockRevisionSchedule) throw new ArgumentException("Revision schedules cannot be placed directly.");
                var existingSchedule = new FilteredElementCollector(document).OfClass(typeof(ScheduleSheetInstance))
                    .Cast<ScheduleSheetInstance>().FirstOrDefault(instance => instance.ScheduleId == schedule.Id && !instance.IsTitleblockRevisionSchedule);
                if (existingSchedule is not null)
                    throw new ArgumentException($"View '{view.Name}' is already on another sheet.");
            }
            else if (!Viewport.CanAddViewToSheet(document, sheet.Id, view.Id))
                throw new ArgumentException($"View '{view.Name}' cannot be placed or is already on another sheet.");
            var width = Math.Max(gap, view.Outline.Max.U - view.Outline.Min.U);
            var height = Math.Max(gap, view.Outline.Max.V - view.Outline.Min.V);
            if (!placement.XMm.HasValue && cursorX + width > outline.Max.U - gap)
            {
                cursorX = outline.Min.U + gap;
                cursorY -= rowHeight + gap;
                rowHeight = 0;
            }
            var x = placement.XMm.HasValue ? Millimeters(placement.XMm.Value) : cursorX + width / 2;
            var y = placement.YMm.HasValue ? Millimeters(placement.YMm.Value) : cursorY - height / 2;
            if (view is ViewSchedule viewSchedule)
                ScheduleSheetInstance.Create(document, sheet.Id, viewSchedule.Id, new XYZ(x, y, 0));
            else
                Viewport.Create(document, sheet.Id, view.Id, new XYZ(x, y, 0));
            names.Add(view.Name);
            cursorX += width + gap;
            rowHeight = Math.Max(rowHeight, height);
        }
        return new ActionResultData { Category = "Sheet", Names = names, Count = names.Count, Verification = new ActionVerification { Changed = [RevitValueReader.GetId(sheet.Id)] } };
    }

    private static BoundingBoxXYZ ResolveBox(Document document, ActionJobContract action)
    {
        if (action.Box is not null)
            return new BoundingBoxXYZ
            {
                Min = Point(action.Box.MinMm),
                Max = Point(action.Box.MaxMm)
            };
        var boxes = action.ElementIds.Select(id => ActionCommandExecutor.CreateId(id).ToElement(document)
            ?? throw new ArgumentException($"Element {id} was not found."))
            .Select(element => element.get_BoundingBox(null)
                ?? throw new ArgumentException($"Element {RevitValueReader.GetId(element.Id)} has no bounding box.")).ToList();
        var padding = Millimeters(1000);
        return new BoundingBoxXYZ
        {
            Min = new XYZ(boxes.Min(box => box.Min.X) - padding, boxes.Min(box => box.Min.Y) - padding, boxes.Min(box => box.Min.Z) - padding),
            Max = new XYZ(boxes.Max(box => box.Max.X) + padding, boxes.Max(box => box.Max.Y) + padding, boxes.Max(box => box.Max.Z) + padding)
        };
    }

    private static XYZ Point(IReadOnlyList<double> values) => new(Millimeters(values[0]), Millimeters(values[1]), Millimeters(values[2]));

    private static View FindView(Document document, string reference) =>
        ReadCommandReader.FindView(document, reference) ?? throw new ArgumentException($"View '{reference}' was not found.");

    private static void SetViewName(Document document, View view, string? name)
    {
        if (name is null) return;
        using var views = document.CollectElements().OfClass<View>();
        if (views.Cast<View>().Any(candidate => candidate.Id != view.Id && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"View name '{name}' already exists.");
        view.Name = name;
    }

    private static bool TemplateMatches(Document document, View view, string name)
    {
        using var views = document.CollectElements().OfClass<View>();
        var template = views.Cast<View>().FirstOrDefault(candidate => candidate.IsTemplate &&
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"View template '{name}' was not found.");
        return view.ViewType == template.ViewType;
    }

    private static void ApplyTemplate(Document document, View view, string name)
    {
        using var views = document.CollectElements().OfClass<View>();
        var template = views.Cast<View>().FirstOrDefault(candidate => candidate.IsTemplate &&
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"View template '{name}' was not found.");
        if (view.ViewType != template.ViewType)
            throw new ArgumentException($"View '{view.Name}' does not match template '{name}' type.");
        view.ViewTemplateId = template.Id;
    }

    internal static ActionResultData SetParameter(Document document, ActionJobContract action)
    {
        var element = CreateId(action.ElementId).ToElement(document)
                      ?? throw new ArgumentException($"Element {action.ElementId} was not found.");
        var (parameter, candidate) = ResolveParameter(element, action.Parameter!, action.ParameterId);
        if (parameter.IsReadOnly) throw new InvalidOperationException($"Parameter '{action.Parameter}' is read-only.");
        var oldValue = ParameterValue(parameter);
        var value = ParameterResolution.ValidateValue(action.Parameter!, parameter.StorageType.ToString(), action.Value!);
        var changed = parameter.StorageType switch
        {
            StorageType.String => parameter.Set((string)value),
            StorageType.Integer => parameter.Set((int)value),
            StorageType.Double => parameter.Set(ParameterDouble(parameter, (double)value)),
            _ => throw new ArgumentException("Only String, Integer and Double parameters are supported; ElementId parameters cannot be set.")
        };
        if (!changed)
            throw new InvalidOperationException($"Revit could not set parameter '{action.Parameter}'.");
        return new ActionResultData
        {
            OldValue = oldValue,
            NewValue = ParameterValue(parameter),
            ParameterScope = candidate.Owner
        };
    }

    internal static (Parameter Parameter, ParameterCandidate Candidate) ResolveParameter(Element element, string name, string? parameterId)
    {
        var found = new List<(Parameter Parameter, ParameterCandidate Candidate)>();
        void Collect(Element owner, string scope)
        {
            foreach (Parameter parameter in owner.Parameters)
            {
                var id = RevitValueReader.GetId(parameter.Id);
                var builtInName = id is >= int.MinValue and < 0
                    ? Enum.GetName(typeof(BuiltInParameter), (int)id) : null;
                var kind = parameter.IsShared ? "shared" : builtInName is not null ? "built-in" : "project";
                var stableId = parameter.IsShared ? parameter.GUID.ToString("D")
                    : builtInName ?? id.ToString(CultureInfo.InvariantCulture);
                found.Add((parameter, new ParameterCandidate(stableId, parameter.Definition.Name,
                    parameter.StorageType.ToString(), scope, kind, builtInName, id > 0 ? id : null)));
            }
        }
        Collect(element, "instance");
        var type = element.Document.GetElement(element.GetTypeId());
        if (type is not null) Collect(type, "type");
        var selected = ParameterResolution.Resolve(name, parameterId, found.Select(item => item.Candidate));
        return found.First(item => item.Candidate == selected);
    }

    private static double ParameterDouble(Parameter parameter, double value)
    {
        var spec = parameter.Definition.GetDataType();
        if (spec == SpecTypeId.Length) return Millimeters(value);
        if (spec == SpecTypeId.Area) return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.SquareMeters);
        return value;
    }

    internal static string ParameterValue(Parameter parameter)
    {
        if (!parameter.HasValue) return string.Empty;
        if (parameter.StorageType == StorageType.String) return parameter.AsString() ?? string.Empty;
        if (parameter.StorageType == StorageType.Integer) return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
        if (parameter.StorageType != StorageType.Double) throw new ArgumentException("Unsupported parameter storage type.");
        var spec = parameter.Definition.GetDataType();
        var value = parameter.AsDouble();
        if (spec == SpecTypeId.Length) value = value.ToMillimeters();
        else if (spec == SpecTypeId.Area) value = UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.SquareMeters);
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static Level FindLevel(Document document, string name)
    {
        using var levels = document.CollectElements().OfClass<Level>()
            .WhereParameter(BuiltInParameter.DATUM_TEXT).Equals(name);
        return levels.FirstOrDefault() as Level
               ?? throw new ArgumentException($"Level '{name}' was not found.");
    }

    private static ElementId CreateId(long value) => ActionCommandExecutor.CreateId(value);
    private static double Millimeters(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

    internal sealed class FamilyNotLoadedException(string name, List<string> closestFamilies)
        : ArgumentException($"Family '{name}' is not loaded. Closest loaded families: {string.Join(", ", closestFamilies)}")
    {
        public List<string> ClosestFamilies { get; } = closestFamilies;
    }
}
