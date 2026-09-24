using Autodesk.Revit.DB;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class ViewVisibility
{
    public static ActionResultData Execute(Document document, ViewVisibilityOptions options, bool dryRun,
        ActionCommandExecutor.ActionFailures failures)
    {
        var source = ReadCommandReader.FindView(document, options.View)
            ?? throw new ArgumentException($"View '{options.View}' was not found.");
        if (source.IsTemplate) throw new ArgumentException("Select a non-template view.");
        var categories = ViewInfoReader.Categories(document);
        var typed = CategoryTypeExpansion.Expand(options.HideCategoriesByType,
            categories.Select(category => (category.Name, CategoryType(category))));
        var requested = options.HideCategories.Concat(options.ShowCategories).Concat(typed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var reference in requested)
        {
            if (ResolveCategory(reference, categories) is not null) continue;
            var matches = ActionJobParser.ClosestFamilyNames(reference, categories.Select(category => category.Name));
            throw new ArgumentException($"Unknown category '{reference}'. Close matches: {string.Join(", ", matches)}.");
        }
        var template = document.GetElement(source.ViewTemplateId) as View;
        if (template is null && options.TemplateMode == "edit_template")
            throw new ArgumentException("The view has no template to edit.");
        if (template is not null && options.TemplateMode is null && TemplateControlsVisibility(template))
            throw new ArgumentException("The view has a template. Choose template_mode: detach, edit_template, or duplicate_view before changing visibility or worksets.");
        using var transaction = new Transaction(document, "revit_set_view_visibility");
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the visibility transaction.");
        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        try
        {
            var target = source;
            var result = new ViewVisibilityResult();
            if (options.TemplateMode == "duplicate_view")
            {
                var duplicateId = source.Duplicate(ViewDuplicateOption.Duplicate);
                target = (View)document.GetElement(duplicateId);
                target.ViewTemplateId = ElementId.InvalidElementId;
                Record(result, "duplicatedFrom", RevitValueReader.GetId(source.Id).ToString(), RevitValueReader.GetId(target.Id).ToString());
            }
            else if (template is not null)
            {
                if (options.TemplateMode == "detach")
                {
                    source.ViewTemplateId = ElementId.InvalidElementId;
                    Record(result, "template", template.Name, "none");
                }
                else if (options.TemplateMode == "edit_template")
                {
                    target = template;
                    using var views = new FilteredElementCollector(document).OfClass(typeof(View));
                    result.AffectedViews = views.Cast<View>().Where(view => view.ViewTemplateId == template.Id)
                        .Select(view => view.Name).OrderBy(name => name).ToList();
                }
            }
            foreach (var (name, hidden) in options.CategoryClasses)
            {
                var before = GetClass(target, name);
                if (before == hidden) continue;
                SetClass(target, name, hidden);
                Record(result, $"categoryClasses.{name}", before.ToString(), hidden.ToString());
            }
            foreach (var reference in options.HideCategories.Concat(typed).Distinct(StringComparer.OrdinalIgnoreCase))
                SetCategory(result, target, ResolveCategory(reference, categories)!, true);
            foreach (var reference in options.ShowCategories.Distinct(StringComparer.OrdinalIgnoreCase))
                SetCategory(result, target, ResolveCategory(reference, categories)!, false);
            if (options.Worksets.HideMask.Count + options.Worksets.ShowMask.Count > 0 && !document.IsWorkshared)
                throw new ArgumentException("Workset masks require a workshared document.");
            if (document.IsWorkshared)
            {
                using var collector = new FilteredWorksetCollector(document).OfKind(WorksetKind.UserWorkset);
                foreach (var workset in collector)
                {
                    var hide = options.Worksets.HideMask.Any(mask => WorksetMask.Matches(workset.Name, mask));
                    var show = options.Worksets.ShowMask.Any(mask => WorksetMask.Matches(workset.Name, mask));
                    if (hide && show) throw new ArgumentException($"Workset '{workset.Name}' matches hide and show masks.");
                    if (!hide && !show) continue;
                    result.MatchedWorksets.Add(workset.Name);
                    var before = target.GetWorksetVisibility(workset.Id);
                    var after = hide ? WorksetVisibility.Hidden : WorksetVisibility.Visible;
                    if (before == after) continue;
                    target.SetWorksetVisibility(workset.Id, after);
                    Record(result, $"worksets.{workset.Name}", before.ToString(), after.ToString());
                }
            }
            foreach (var filter in options.Filters)
            {
                var ids = target.GetFilters().Where(id => string.Equals(document.GetElement(id)?.Name, filter.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (ids.Count != 1) throw new ArgumentException($"Filter '{filter.Name}' is not uniquely applied to the view.");
                var before = target.GetFilterVisibility(ids[0]);
                if (before == filter.Visible) continue;
                target.SetFilterVisibility(ids[0], filter.Visible);
                Record(result, $"filters.{filter.Name}", before.ToString(), filter.Visible.ToString());
            }
            result.ViewId = RevitValueReader.GetId(target.Id);
            result.ViewName = target.Name;
            var status = dryRun ? transaction.RollBack() : transaction.Commit();
            if (status != (dryRun ? TransactionStatus.RolledBack : TransactionStatus.Committed))
                throw new InvalidOperationException(failures.Message ?? "Visibility transaction failed.");
            return new ActionResultData { Visibility = result, DryRun = dryRun, RolledBack = dryRun, Committed = !dryRun };
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }

    private static void SetCategory(ViewVisibilityResult result, View view, Category category, bool hidden)
    {
        if (!view.CanCategoryBeHidden(category.Id))
        {
            result.CategoryFailures.Add($"{category.Name}: CanCategoryBeHidden returned false");
            return;
        }
        var before = view.GetCategoryHidden(category.Id);
        if (before == hidden) return;
        view.SetCategoryHidden(category.Id, hidden);
        Record(result, $"categories.{category.Name}", before.ToString(), hidden.ToString());
    }

    private static Category? ResolveCategory(string reference, List<Category> categories) =>
        categories.FirstOrDefault(category => string.Equals(category.Name, reference, StringComparison.OrdinalIgnoreCase)
            || long.TryParse(reference, out var id) && RevitValueReader.GetId(category.Id) == id);

    private static string CategoryType(Category category) => category.CategoryType.ToString() switch
    {
        "AnalyticalModel" => "analytical", "Model" => "model", "Annotation" => "annotation", "Import" => "import", _ => "other"
    };

    private static bool GetClass(View view, string name) => name switch
    {
        "model" => view.AreModelCategoriesHidden,
        "annotation" => view.AreAnnotationCategoriesHidden,
        "analytical" => view.AreAnalyticalModelCategoriesHidden,
        "import" => view.AreImportCategoriesHidden,
        "point_clouds" => view.ArePointCloudsHidden,
        _ => throw new ArgumentException($"Unknown category class '{name}'.")
    };

    private static void SetClass(View view, string name, bool hidden)
    {
        switch (name)
        {
            case "model": view.AreModelCategoriesHidden = hidden; break;
            case "annotation": view.AreAnnotationCategoriesHidden = hidden; break;
            case "analytical": view.AreAnalyticalModelCategoriesHidden = hidden; break;
            case "import": view.AreImportCategoriesHidden = hidden; break;
            case "point_clouds": view.ArePointCloudsHidden = hidden; break;
        }
    }

    private static void Record(ViewVisibilityResult result, string setting, string before, string after) =>
        result.Changes.Add(new VisibilityChange { Setting = setting, Before = before, After = after });

    private static bool TemplateControlsVisibility(View template)
    {
        var nonControlled = template.GetNonControlledTemplateParameterIds().ToHashSet();
        return template.GetTemplateParameterIds().Where(id => !nonControlled.Contains(id))
            .Select(id => Enum.GetName(typeof(BuiltInParameter), RevitValueReader.GetId(id)) ?? "")
            .Any(name => name.Contains("VIS_GRAPHICS") || name.Contains("WORKSET") || name.Contains("FILTER"));
    }
}
