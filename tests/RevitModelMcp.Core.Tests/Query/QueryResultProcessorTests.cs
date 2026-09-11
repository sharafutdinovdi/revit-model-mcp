using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Core.Tests.Query;

public sealed class QueryResultProcessorTests
{
    [Test]
    public async Task SortAndPage_SortsNumericFieldAndReportsMoreRows()
    {
        var records = new[]
        {
            Record(1, ("Thickness", "300", 300d, "mm")),
            Record(2, ("Thickness", "100", 100d, "mm")),
            Record(3, ("Thickness", "200", 200d, "mm"))
        };

        var page = QueryResultProcessor.SortAndPage(
            records,
            new QuerySortSpec { Field = "thickness" },
            1,
            1,
            out var hasMore);

        await Assert.That(page.Single().Id).IsEqualTo(3L);
        await Assert.That(hasMore).IsTrue();
    }

    [Test]
    public async Task Aggregate_GroupsPreparedRowsAndCalculatesCountSumAverage()
    {
        var records = new[]
        {
            Record(1, ("level", "01", null, null), ("Area", "10", 10d, "m2")),
            Record(2, ("level", "01", null, null), ("Area", "20", 20d, "m2")),
            Record(3, ("level", "02", null, null), ("Area", "9", 9d, "m2"))
        };

        var result = QueryResultProcessor.Aggregate(records, new[] { "level" }, "Area");

        await Assert.That(result.MatchedElements).IsEqualTo(3);
        await Assert.That(result.Groups).Count().IsEqualTo(2);
        var first = result.Groups.Single(group => group.Keys["level"] == "01");
        await Assert.That(first.Count).IsEqualTo(2);
        await Assert.That(first.NumericCount).IsEqualTo(2);
        await Assert.That(first.Sum).IsEqualTo(30d);
        await Assert.That(first.Average).IsEqualTo(15d);
        await Assert.That(first.Unit).IsEqualTo("m2");
    }

    [Test]
    public async Task Resolve_UnknownParameter_ReturnsCatalogHint()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            QueryParameterValidator.Resolve("ADSK_Нет", new[] { "ADSK_Номер корпуса", "Марка" }));

        await Assert.That(exception.Message).Contains("list-catalog");
        await Assert.That(exception.Message).Contains("ADSK_Нет");
    }

    [Test]
    public async Task ValidateAggregateFields_UnknownNumericField_ReturnsCatalogHint()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            QueryParameterValidator.ValidateAggregateFields(
                new[] { "level" },
                "Площадь",
                new[] { "level", "Area" }));

        await Assert.That(exception.Message).Contains("Параметр «Площадь» не найден");
        await Assert.That(exception.Message).Contains("list-catalog с section=parameters");
    }

    [Test]
    public async Task Aggregate_KnownNumericFieldWithoutNumbers_ReportsFieldFound()
    {
        const string numericField = "Area";
        QueryParameterValidator.ValidateAggregateFields(
            new[] { "level" },
            numericField,
            new[] { "level", numericField });
        var records = new[]
        {
            Record(1, ("level", "01", null, null), (numericField, null, null, null))
        };

        var result = QueryResultProcessor.Aggregate(records, new[] { "level" }, numericField);

        await Assert.That(result.NumericFieldFound).IsTrue();
        await Assert.That(result.Groups.Single().NumericCount).IsEqualTo(0);
        await Assert.That(result.Groups.Single().Sum).IsNull();
    }

    [Test]
    public async Task ValidateAggregateFields_UnknownGroupField_ReturnsCatalogHint()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            QueryParameterValidator.ValidateAggregateFields(
                new[] { "unknown-level" },
                null,
                new[] { "level", "Area" }));

        await Assert.That(exception.Message).Contains("Параметр «unknown-level» не найден");
        await Assert.That(exception.Message).Contains("list-catalog с section=parameters");
    }

    [Test]
    public async Task ResolveCategory_LocalizedAndEnglishNames_ReturnSameCategory()
    {
        var categories = new[]
        {
            new CategoryCandidate("rooms", new[] { "Rooms", "Помещения" }, "OST_Rooms"),
            new CategoryCandidate("walls", new[] { "Walls", "Стены" }, "OST_Walls")
        };

        var localized = ResolveCategory("Помещения", categories);
        var english = ResolveCategory("Rooms", categories);

        await Assert.That(localized.Id).IsEqualTo("rooms");
        await Assert.That(english.Id).IsEqualTo(localized.Id);
    }

    [Test]
    public async Task ResolveCategory_UnknownName_ReturnsCatalogHint()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ResolveCategory(
                "Неизвестная",
                new[] { new CategoryCandidate("rooms", new[] { "Rooms", "Помещения" }, "OST_Rooms") }));

        await Assert.That(exception.Message).Contains("Категория «Неизвестная» не найдена");
        await Assert.That(exception.Message).Contains("list-catalog");
        await Assert.That(exception.Message).Contains("section=categories");
    }

    private static PreparedQueryRecord Record(
        long id,
        params (string Name, string? Text, double? Number, string? Unit)[] values)
    {
        return new PreparedQueryRecord
        {
            Id = id,
            Values = values.ToDictionary(
                value => value.Name,
                value => new PreparedQueryValue { Text = value.Text, Number = value.Number, Unit = value.Unit },
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static CategoryCandidate ResolveCategory(
        string requested,
        IEnumerable<CategoryCandidate> categories) =>
        CategoryNameResolver.Resolve(
            requested,
            categories,
            category => category.RevitNames,
            category => category.BuiltInName);

    private sealed record CategoryCandidate(string Id, IReadOnlyList<string> RevitNames, string BuiltInName);
}
