using Autodesk.Revit.DB;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class CatalogReader
{
    public static CatalogData Read(Document document, string section)
    {
        var items = section.ToLowerInvariant() switch
        {
            "categories" => ReadCategories(document),
            "family-types" => ReadFamilyTypes(document),
            "levels" => ReadLevels(document),
            "area-schemes" => ReadAreaSchemes(document),
            "views" => ReadViews(document),
            "worksets" => ReadWorksets(document),
            "phases" => ReadPhases(document),
            "parameters" => ReadParameters(document),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Неизвестный раздел catalog.")
        };
        return new CatalogData { Section = section, Items = items };
    }

    private static List<CatalogItem> ReadCategories(Document document)
    {
        var counts = new Dictionary<long, int>();
        foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            if (element.Category is not null)
            {
                var id = RevitValueReader.GetId(element.Category.Id);
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return document.Settings.Categories.Cast<Category>()
            .Where(category => counts.ContainsKey(RevitValueReader.GetId(category.Id)))
            .Select(category => new CatalogItem
            {
                Id = RevitValueReader.GetId(category.Id),
                Name = category.Name,
                Count = counts[RevitValueReader.GetId(category.Id)]
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CatalogItem> ReadFamilyTypes(Document document)
    {
        return new FilteredElementCollector(document).WhereElementIsElementType()
            .Select(type => new CatalogItem
            {
                Id = RevitValueReader.GetId(type.Id),
                Name = type.Name,
                Category = type.Category?.Name,
                Family = RevitValueReader.GetFamilyName(type),
                Type = type.Name
            })
            .Where(item => item.Category is not null)
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CatalogItem> ReadLevels(Document document)
    {
        return new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(level => level.Elevation)
            .Select(level => new CatalogItem { Id = RevitValueReader.GetId(level.Id), Name = level.Name })
            .ToList();
    }

    private static List<CatalogItem> ReadAreaSchemes(Document document)
    {
        var counts = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_Areas)
            .WhereElementIsNotElementType().OfType<Area>()
            .GroupBy(area => RevitValueReader.GetId(area.AreaScheme.Id))
            .ToDictionary(group => group.Key, group => group.Count());
        return new FilteredElementCollector(document).OfClass(typeof(AreaScheme)).Cast<AreaScheme>()
            .Select(scheme => new CatalogItem
            {
                Id = RevitValueReader.GetId(scheme.Id),
                Name = scheme.Name,
                IsGrossBuildingArea = scheme.IsGrossBuildingArea,
                Count = counts.GetValueOrDefault(RevitValueReader.GetId(scheme.Id))
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CatalogItem> ReadViews(Document document)
    {
        return new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
            .Select(view => new CatalogItem
            {
                Id = RevitValueReader.GetId(view.Id),
                Name = view.Name,
                Type = view.ViewType.ToString(),
                IsTemplate = view.IsTemplate
            })
            .OrderByDescending(item => item.IsTemplate)
            .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CatalogItem> ReadWorksets(Document document)
    {
        if (!document.IsWorkshared)
        {
            return new List<CatalogItem>();
        }

        return new FilteredWorksetCollector(document).ToWorksets()
            .Select(workset => new CatalogItem
            {
                Id = workset.Id.IntegerValue,
                Name = workset.Name,
                Type = workset.Kind.ToString()
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CatalogItem> ReadPhases(Document document)
    {
        return document.Phases.Cast<Phase>()
            .Select(phase => new CatalogItem { Id = RevitValueReader.GetId(phase.Id), Name = phase.Name })
            .ToList();
    }

    private static List<CatalogItem> ReadParameters(Document document)
    {
        var parameters = new Dictionary<string, ParameterAccumulator>(StringComparer.OrdinalIgnoreCase);
        ReadBoundParameters(document, parameters);
        foreach (var type in new FilteredElementCollector(document).WhereElementIsElementType())
        {
            AddElementParameters(type, parameters);
        }

        var sampledTypes = new HashSet<long>();
        var sampledCategories = new HashSet<long>();
        foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            var typeId = RevitValueReader.IsValidId(element.GetTypeId())
                ? RevitValueReader.GetId(element.GetTypeId())
                : 0;
            var categoryId = element.Category is null ? 0 : RevitValueReader.GetId(element.Category.Id);
            if ((typeId != 0 && sampledTypes.Add(typeId)) || (categoryId != 0 && sampledCategories.Add(categoryId)))
            {
                AddElementParameters(element, parameters);
            }
        }

        return parameters.Values.Select(parameter => new CatalogItem
            {
                Name = parameter.Name,
                ValueType = string.Join(", ", parameter.ValueTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                Categories = parameter.Categories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList()
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadBoundParameters(
        Document document,
        IDictionary<string, ParameterAccumulator> parameters)
    {
        var iterator = document.ParameterBindings.ForwardIterator();
        iterator.Reset();
        while (iterator.MoveNext())
        {
            var definition = iterator.Key;
            var accumulator = Get(parameters, definition.Name);
            accumulator.ValueTypes.Add(definition.GetDataType().TypeId);
            if (iterator.Current is ElementBinding binding)
            {
                foreach (Category category in binding.Categories)
                {
                    accumulator.Categories.Add(category.Name);
                }
            }
        }
    }

    private static void AddElementParameters(Element element, IDictionary<string, ParameterAccumulator> parameters)
    {
        foreach (Parameter parameter in element.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var accumulator = Get(parameters, name!);
            accumulator.ValueTypes.Add(parameter.StorageType.ToString());
            if (element.Category is not null)
            {
                accumulator.Categories.Add(element.Category.Name);
            }
        }
    }

    private static ParameterAccumulator Get(IDictionary<string, ParameterAccumulator> parameters, string name)
    {
        if (!parameters.TryGetValue(name, out var accumulator))
        {
            accumulator = new ParameterAccumulator(name);
            parameters[name] = accumulator;
        }

        return accumulator;
    }

    private sealed class ParameterAccumulator
    {
        public ParameterAccumulator(string name) => Name = name;
        public string Name { get; }
        public HashSet<string> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ValueTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
