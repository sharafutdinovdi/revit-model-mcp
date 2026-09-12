using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Capture;

internal static class ParameterFillReader
{
    public static ParameterFillData Read(Document document, ControlJobContract job)
    {
        var context = QueryFilterBuilder.Build(document, new ElementFilterSpec
        {
            Categories = job.Categories!,
            Level = job.Level,
            Workset = job.Workset,
            View = job.View
        }, Array.Empty<string>());
        var result = new ParameterFillData
        {
            Scope = new ParameterFillScope
            {
                Categories = job.Categories!,
                Level = job.Level,
                Workset = job.Workset,
                View = job.View
            },
            Parameters = job.Parameters!.Select(name => new ParameterFillItem { Name = name }).ToList()
        };
        var types = new Dictionary<long, Element?>();
        using var collector = context.CreateCollector();
        foreach (var element in collector)
        {
            result.Scope.Elements++;
            foreach (var item in result.Parameters)
            {
                item.Elements++;
                var categoryName = element.Category?.Name ?? string.Empty;
                var category = item.ByCategory.FirstOrDefault(entry => entry.Category == categoryName);
                if (category is null)
                {
                    category = new ParameterCategoryFill { Category = categoryName };
                    item.ByCategory.Add(category);
                }
                category.Elements++;
                var parameter = element.LookupParameter(item.Name);
                var fromType = false;
                if (parameter is null && (job.IncludeTypes ?? true))
                {
                    var typeId = element.GetTypeId();
                    var key = RevitValueReader.GetId(typeId);
                    if (!types.TryGetValue(key, out var type))
                    {
                        type = RevitValueReader.IsValidId(typeId) ? document.GetElement(typeId) : null;
                        types.Add(key, type);
                    }
                    parameter = type?.LookupParameter(item.Name);
                    fromType = parameter is not null;
                }
                if (parameter is null)
                {
                    item.Missing++;
                    category.Missing++;
                    AddSample(item.MissingSampleIds, element.Id, job.SampleLimit ?? 20);
                    continue;
                }
                if (fromType) item.Owner.Type++;
                else item.Owner.Instance++;
                var storage = parameter.StorageType.ToString();
                item.StorageTypes.TryGetValue(storage, out var count);
                item.StorageTypes[storage] = count + 1;
                var filled = parameter.HasValue && (parameter.StorageType switch
                {
                    StorageType.String => RevitValueReader.GetParameterText(parameter) is not null,
                    StorageType.ElementId => RevitValueReader.IsValidId(parameter.AsElementId()),
                    StorageType.Double or StorageType.Integer => true,
                    _ => false
                });
                if (filled)
                {
                    item.Filled++;
                    category.Filled++;
                }
                else
                {
                    item.Empty++;
                    category.Empty++;
                    AddSample(item.EmptySampleIds, element.Id, job.SampleLimit ?? 20);
                }
            }
        }
        return result;
    }

    private static void AddSample(List<long> samples, ElementId id, int limit)
    {
        if (samples.Count < limit) samples.Add(RevitValueReader.GetId(id));
    }
}
