using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Query;

public static class QueryResultProcessor
{
    public static IReadOnlyList<PreparedQueryRecord> SortAndPage(
        IReadOnlyList<PreparedQueryRecord> records,
        QuerySortSpec sort,
        int offset,
        int limit,
        out bool hasMore)
    {
        if (records is null)
        {
            throw new ArgumentNullException(nameof(records));
        }

        IOrderedEnumerable<PreparedQueryRecord> ordered;
        if (string.Equals(sort.Field, "id", StringComparison.OrdinalIgnoreCase))
        {
            ordered = sort.Descending
                ? records.OrderByDescending(record => record.Id)
                : records.OrderBy(record => record.Id);
        }
        else
        {
            Func<PreparedQueryRecord, PreparedQueryValue?> value = record =>
                GetValue(record, sort.Field);
            ordered = sort.Descending
                ? records.OrderByDescending(value, PreparedValueComparer.Instance)
                : records.OrderBy(value, PreparedValueComparer.Instance);
            ordered = ordered.ThenBy(record => record.Id);
        }

        var all = ordered.ToList();
        var page = PageSlice.Create(all, offset, limit);
        hasMore = page.HasMore;
        return page.Items;
    }

    public static AggregateElementsData Aggregate(
        IReadOnlyList<PreparedQueryRecord> records,
        IReadOnlyList<string> groupBy,
        string? numericField)
    {
        if (groupBy.Count is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(groupBy), "One or two grouping fields are required.");
        }

        var result = new AggregateElementsData
        {
            MatchedElements = records.Count,
            GroupBy = groupBy.ToList(),
            NumericField = numericField,
            NumericFieldFound = string.IsNullOrWhiteSpace(numericField) ? null : true
        };
        var groups = records.GroupBy(record => BuildGroupKey(record, groupBy), StringComparer.Ordinal);
        foreach (var group in groups.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            var numbers = string.IsNullOrWhiteSpace(numericField)
                ? new List<PreparedQueryValue>()
                : group.Select(record => GetValue(record, numericField!))
                    .Where(value => value?.Number is not null)
                    .Select(value => value!)
                    .ToList();
            result.Groups.Add(new AggregateGroup
            {
                Keys = groupBy.ToDictionary(
                    field => field,
                    field => GetValue(first, field)?.Text,
                    StringComparer.OrdinalIgnoreCase),
                Count = group.Count(),
                NumericCount = numbers.Count,
                Sum = numbers.Count == 0 ? null : numbers.Sum(value => value.Number!.Value),
                Average = numbers.Count == 0 ? null : numbers.Average(value => value.Number!.Value),
                Unit = numbers.Select(value => value.Unit).FirstOrDefault(unit => !string.IsNullOrWhiteSpace(unit))
            });
        }

        return result;
    }

    private static string BuildGroupKey(PreparedQueryRecord record, IEnumerable<string> fields)
    {
        return string.Join("\u001f", fields.Select(field => GetValue(record, field)?.Text ?? string.Empty));
    }

    private static PreparedQueryValue? GetValue(PreparedQueryRecord record, string field)
    {
        return record.Values.TryGetValue(field, out var value) ? value : null;
    }

    private sealed class PreparedValueComparer : IComparer<PreparedQueryValue?>
    {
        public static PreparedValueComparer Instance { get; } = new();

        public int Compare(PreparedQueryValue? x, PreparedQueryValue? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            if (x.Number.HasValue && y.Number.HasValue)
            {
                return x.Number.Value.CompareTo(y.Number.Value);
            }

            return StringComparer.OrdinalIgnoreCase.Compare(x.Text, y.Text);
        }
    }
}
