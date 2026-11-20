using Autodesk.Revit.DB;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Capture;

internal static class ElementQueryReader
{
    private static readonly string[] DefaultFields = { "category", "family", "type", "name", "level" };

    public static QueryElementsData ReadQuery(Document document, ControlJobParseResult job)
    {
        if (job.Link is not null || job.UniqueId is not null) return ReadSelectedDocuments(document, job);
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
            preparedPage = page.Items.Select(id => ReadElement(document, id))
                .Where(element => element is not null)
                .Select(element => reader.Prepare(element!, fields))
                .ToList();
        }
        else
        {
            var sortable = ids.Select(id => ReadElement(document, id))
                .Where(element => element is not null)
                .Select(element => reader.Prepare(element!, new[] { job.Sort.Field }))
                .ToList();
            var page = QueryResultProcessor.SortAndPage(sortable, job.Sort, job.Offset, job.Limit, out hasMore);
            preparedPage = page.Select(record => ReadElement(document, CreateElementId(record.Id)))
                .Where(element => element is not null)
                .Select(element => reader.Prepare(element!, fields))
                .ToList();
        }

        var elements = preparedPage.Select(reader.ToOutput).ToList();
        if (job.IncludeGeometry)
        {
            foreach (var item in elements)
            {
                var element = ReadElement(document, CreateElementId(item.Id));
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

    private static QueryElementsData ReadSelectedDocuments(Document host, ControlJobParseResult job)
    {
        var fields = job.Fields.Count == 0 ? DefaultFields : job.Fields;
        var sortFields = new[] { job.Sort.Field };
        var records = new List<PreparedQueryRecord>();
        var sources = LinkedElementReader.Sources(host, job.Link);
        var readers = sources.ToDictionary(source => source, source => new ElementFieldReader(source.Document));
        var origins = new Dictionary<PreparedQueryRecord, LinkedReadSource>();
        var total = 0;
        foreach (var source in sources)
        {
            var context = QueryFilterBuilder.Build(source.Document, job.Filters, fields.Concat(sortFields));
            using var collector = context.CreateCollector();
            IEnumerable<ElementId> ids = collector.ToElementIds();
            if (job.UniqueId is not null)
            {
                try
                {
                    var element = source.Document.GetElement(job.UniqueId);
                    ids = element is null ? Array.Empty<ElementId>() : ids.Where(id => id == element.Id);
                }
                catch (Exception exception)
                {
                    PluginLog.Skipped($"link {source.InstanceId} UniqueId lookup", exception);
                    continue;
                }
            }
            foreach (var id in ids)
            {
                total++;
                try
                {
                    var element = ReadElement(source.Document, id);
                    if (element is null) continue;
                    var record = readers[source].Prepare(element, sortFields);
                    record.LinkInstanceId = source.InstanceId;
                    records.Add(record);
                    origins.Add(record, source);
                }
                catch (Exception exception) { PluginLog.Skipped($"link {source.InstanceId} element {RevitValueReader.GetId(id)}", exception); }
            }
        }
        var page = QueryResultProcessor.SortAndPage(records, job.Sort, job.Offset, job.Limit, out var hasMore);
        var result = new QueryElementsData
        {
            Offset = job.Offset,
            Limit = job.Limit,
            Total = total,
            HasMore = hasMore,
            Fields = fields.ToList()
        };
        foreach (var record in page)
        {
            var source = origins[record];
            try
            {
                var element = ReadElement(source.Document, CreateElementId(record.Id));
                if (element is null) continue;
                var reader = readers[source];
                var row = reader.ToOutput(reader.Prepare(element, fields));
                if (job.IncludeGeometry) ViewElementReader.ReadGeometry(element, row);
                source.Shape(row);
                result.Elements.Add(row);
            }
            catch (Exception exception) { PluginLog.Skipped($"link {source.InstanceId} element {record.Id}", exception); }
        }
        return result;
    }

    public static AggregateElementsData ReadAggregate(Document document, ControlJobParseResult job)
    {
        var fields = job.GroupBy.Concat(string.IsNullOrWhiteSpace(job.NumericField)
                ? Array.Empty<string>()
                : new[] { job.NumericField! })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var context = QueryFilterBuilder.Build(document, job.Filters, fields);
        // Schema validation precedes value reads; an empty result must not hide a misspelled name.
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

    private static Element? ReadElement(Document document, ElementId id)
    {
        var element = document.GetElement(id);
        if (element is null)
        {
            PluginLog.Skipped($"element {RevitValueReader.GetId(id)}", new InvalidOperationException("Element is unavailable."));
        }
        return element;
    }
}
