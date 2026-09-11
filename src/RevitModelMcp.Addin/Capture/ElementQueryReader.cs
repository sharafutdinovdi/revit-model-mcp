using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Capture;

internal static class ElementQueryReader
{
    private static readonly string[] DefaultFields = { "category", "family", "type", "name", "level" };

    public static QueryElementsData ReadQuery(Document document, ControlJobParseResult job)
    {
        var fields = job.Fields.Count == 0 ? DefaultFields : job.Fields;
        var resolutionFields = fields.Concat(new[] { job.Sort.Field });
        var context = QueryFilterBuilder.Build(document, job.Filters, resolutionFields);
        var ids = context.CreateCollector().ToElementIds().ToList();
        var reader = new ElementFieldReader(document);
        IReadOnlyList<PreparedQueryRecord> preparedPage;
        bool hasMore;
        if (string.Equals(job.Sort.Field, "id", StringComparison.OrdinalIgnoreCase))
        {
            var orderedIds = job.Sort.Descending
                ? ids.OrderByDescending(RevitValueReader.GetId).ToList()
                : ids.OrderBy(RevitValueReader.GetId).ToList();
            var page = PageSlice.Create(orderedIds, job.Offset, job.Limit);
            hasMore = page.HasMore;
            preparedPage = page.Items.Select(id => document.GetElement(id))
                .Where(element => element is not null)
                .Select(element => reader.Prepare(element!, fields))
                .ToList();
        }
        else
        {
            var sortable = ids.Select(document.GetElement)
                .Where(element => element is not null)
                .Select(element => reader.Prepare(element!, new[] { job.Sort.Field }))
                .ToList();
            var page = QueryResultProcessor.SortAndPage(sortable, job.Sort, job.Offset, job.Limit, out hasMore);
            preparedPage = page.Select(record => document.GetElement(CreateElementId(record.Id)))
                .Where(element => element is not null)
                .Select(element => reader.Prepare(element!, fields))
                .ToList();
        }

        var elements = preparedPage.Select(reader.ToOutput).ToList();
        if (job.IncludeGeometry)
        {
            foreach (var item in elements)
            {
                var element = CreateElementId(item.Id).ToElement(document);
                if (element is not null) ViewElementReader.ReadGeometry(element, item);
            }
        }

        return new QueryElementsData
        {
            Offset = job.Offset,
            Limit = job.Limit,
            Total = ids.Count,
            HasMore = hasMore,
            Fields = fields.ToList(),
            Elements = elements
        };
    }

    public static AggregateElementsData ReadAggregate(Document document, ControlJobParseResult job)
    {
        var fields = job.GroupBy.Concat(string.IsNullOrWhiteSpace(job.NumericField)
                ? Array.Empty<string>()
                : new[] { job.NumericField! })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var context = QueryFilterBuilder.Build(document, job.Filters, fields);
        // Схему проверяем до чтения значений, чтобы пустой результат не маскировал опечатку в имени.
        QueryParameterValidator.ValidateAggregateFields(job.GroupBy, job.NumericField, context.ResolvedFields);
        var reader = new ElementFieldReader(document);
        var records = context.CreateCollector()
            .Select(element => reader.Prepare(element, fields))
            .ToList();
        return QueryResultProcessor.Aggregate(records, job.GroupBy, job.NumericField);
    }

    private static ElementId CreateElementId(long value)
    {
#if REVIT2024_OR_GREATER
        return new ElementId(value);
#else
        return new ElementId(checked((int)value));
#endif
    }
}
