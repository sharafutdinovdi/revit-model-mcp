using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Core.Tests.Query;

public sealed class QueryResultProcessorTests
{
    [Test]
    public async Task Pinned_ResolvesWithoutAParameterAndMatchesBooleanValues()
    {
        await Assert.That(QueryParameterValidator.Resolve("PINNED", [])).IsEqualTo("pinned");
        await Assert.That(QueryParameterValidator.IsBuiltInField("pinned")).IsTrue();
        QueryParameterValidator.ValidateAggregateFields(["pinned"], null, []);
        var filter = new ParameterFilterSpec { Parameter = "pinned", Operator = ParameterOperator.Equals, Value = "true" };
        await Assert.That(QueryParameterValidator.MatchesPinned(true, filter)).IsTrue();
        await Assert.That(QueryParameterValidator.MatchesPinned(false, filter)).IsFalse();
        filter.Value = "false";
        await Assert.That(QueryParameterValidator.MatchesPinned(false, filter)).IsTrue();
        filter.Value = "invalid";
        Assert.Throws<ArgumentException>(() => QueryParameterValidator.MatchesPinned(false, filter));
    }

    [Test]
    [Arguments("instance", "type", "instance")]
    [Arguments("", "type", "")]
    [Arguments(null, "type", "type")]
    [Arguments(null, null, null)]
    public async Task ParameterValueSource_UsesTypeOnlyWhenInstanceParameterIsAbsent(
        string? instanceParameter, string? typeParameter, string? expected)
    {
        var typeReads = 0;
        var result = QueryParameterSemantics.ResolveValueSource(instanceParameter, () =>
        {
            typeReads++;
            return typeParameter;
        });
        await Assert.That(result).IsEqualTo(expected);
        await Assert.That(typeReads).IsEqualTo(instanceParameter is null ? 1 : 0);
    }

    [Test]
    [Arguments(false, "String", "text", null, false)]
    [Arguments(true, "String", null, null, false)]
    [Arguments(true, "String", "", null, false)]
    [Arguments(true, "String", " \t\r\n", null, false)]
    [Arguments(true, "String", "segment", null, true)]
    [Arguments(false, "ElementId", null, 42L, false)]
    [Arguments(true, "ElementId", null, -1L, false)]
    [Arguments(true, "ElementId", null, null, false)]
    [Arguments(true, "ElementId", null, -2L, true)]
    [Arguments(true, "ElementId", null, 42L, true)]
    [Arguments(true, "Integer", null, null, true)]
    [Arguments(true, "Double", null, null, true)]
    [Arguments(false, "Double", null, null, false)]
    [Arguments(true, "None", null, null, false)]
    public async Task ParameterValue_ExcludesUnsetBlankAndInvalidIds(
        bool hasValue, string storageType, string? text, long? elementId, bool expected)
    {
        await Assert.That(QueryParameterSemantics.HasValue(hasValue, storageType, text, elementId)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, false, null, QueryParameterKind.Unknown)]
    [Arguments("generic", false, null, QueryParameterKind.Unknown)]
    [Arguments("autodesk.spec.aec:material-1.0.0", false, null, QueryParameterKind.Unknown)]
    [Arguments("autodesk.spec:spec.string-2.0.0", false, null, QueryParameterKind.String)]
    [Arguments("spec:string", false, null, QueryParameterKind.String)]
    [Arguments("length", true, null, QueryParameterKind.Double)]
    [Arguments("yesno", false, null, QueryParameterKind.Integer)]
    [Arguments("generic", false, "String", QueryParameterKind.String)]
    [Arguments(null, false, "Integer", QueryParameterKind.Integer)]
    [Arguments(null, false, "Double", QueryParameterKind.Double)]
    [Arguments(null, false, "ElementId", QueryParameterKind.ElementId)]
    [Arguments(null, false, "None", QueryParameterKind.Unknown)]
    public async Task ParameterKind_UsesActualStorageForGenericDefinitions(
        string? dataType, bool measurable, string? storageType, QueryParameterKind expected)
    {
        await Assert.That(QueryParameterSemantics.ResolveKind(dataType, measurable, storageType)).IsEqualTo(expected);
    }

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
            Record(1, ("level", "Level 1", null, null), ("Area", "10", 10d, "m2")),
            Record(2, ("level", "Level 1", null, null), ("Area", "20", 20d, "m2")),
            Record(3, ("level", "Level 2", null, null), ("Area", "9", 9d, "m2"))
        };

        var result = QueryResultProcessor.Aggregate(records, new[] { "level" }, "Area");

        await Assert.That(result.MatchedElements).IsEqualTo(3);
        await Assert.That(result.Groups).Count().IsEqualTo(2);
        var first = result.Groups.Single(group => group.Keys["level"] == "Level 1");
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
            QueryParameterValidator.Resolve("Missing Parameter", new[] { "Building Number", "Mark" }));

        await Assert.That(exception.Message).Contains("list-catalog");
        await Assert.That(exception.Message).Contains("Missing Parameter");
    }

    [Test]
    public async Task ValidateAggregateFields_UnknownNumericField_ReturnsCatalogHint()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            QueryParameterValidator.ValidateAggregateFields(
                new[] { "level" },
                "Missing Area",
                new[] { "level", "Area" }));

        await Assert.That(exception.Message).Contains("Parameter 'Missing Area' was not found");
        await Assert.That(exception.Message).Contains("list-catalog with section=parameters");
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
            Record(1, ("level", "Level 1", null, null), (numericField, null, null, null))
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

        await Assert.That(exception.Message).Contains("Parameter 'unknown-level' was not found");
        await Assert.That(exception.Message).Contains("list-catalog with section=parameters");
    }

    [Test]
    public async Task ResolveCategory_DisplayNameAliases_ReturnSameCategory()
    {
        var categories = new[]
        {
            new CategoryCandidate("rooms", new[] { "Rooms", "Room Spaces" }, "OST_Rooms"),
            new CategoryCandidate("walls", new[] { "Walls", "Wall Elements" }, "OST_Walls")
        };

        var displayName = ResolveCategory("Room Spaces", categories);
        var alias = ResolveCategory("Rooms", categories);

        await Assert.That(displayName.Id).IsEqualTo("rooms");
        await Assert.That(alias.Id).IsEqualTo(displayName.Id);
    }

    [Test]
    public async Task ResolveCategory_UnknownName_ReturnsCatalogHint()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ResolveCategory(
                "Unknown",
                new[] { new CategoryCandidate("rooms", new[] { "Rooms", "Room Spaces" }, "OST_Rooms") }));

        await Assert.That(exception.Message).Contains("Category 'Unknown' was not found");
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
