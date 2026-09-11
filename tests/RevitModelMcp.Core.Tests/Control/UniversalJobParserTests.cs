using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class UniversalJobParserTests
{
    [Test]
    [Arguments("{}", false)]
    [Arguments("{\"includeGeometry\":false}", false)]
    [Arguments("{\"includeGeometry\":true}", true)]
    public async Task Parse_QueryElements_GeometryIsOptIn(string options, bool expected)
    {
        var json = options == "{}" ? "{\"command\":\"query-elements\"}"
            : options.Insert(1, "\"command\":\"query-elements\",");
        var result = ControlJobParser.Parse(json);
        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.QueryElements);
        await Assert.That(result.IncludeGeometry).IsEqualTo(expected);
    }

    [Test]
    public async Task Parse_QueryElements_ReadsEveryFilterAndOutputControl()
    {
        const string json = """
                            {
                              "command": "query-elements",
                              "categories": ["Помещения", " Стены ", "помещения"],
                              "family": "Basic Wall",
                              "type": "200 mm",
                              "level": "03",
                              "view": "План 3",
                              "workset": "АР",
                              "phase": "Новая конструкция",
                              "areaScheme": "СПП в ГНС",
                              "parameterFilters": [
                                {"parameter":"Mark", "operator":"equals", "value":"A"},
                                {"parameter":"Comments", "operator":"contains", "value":"тест"},
                                {"parameter":"Thickness", "operator":"greater", "value":"200"},
                                {"parameter":"Area", "operator":"less", "value":"15.5"},
                                {"parameter":"ADSK_Номер корпуса", "operator":"empty"},
                                {"parameter":"Number", "operator":"not-empty"},
                                {"parameter":"RUS_Code", "operator":"exists"}
                              ],
                              "fields": ["id", "category", "ADSK_Номер корпуса"],
                              "offset": 20,
                              "limit": 10,
                              "sort": {"field":"level", "direction":"desc"}
                            }
                            """;

        var result = ControlJobParser.Parse(json);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.QueryElements);
        await Assert.That(result.Filters.Categories).IsEquivalentTo(new[] { "Помещения", "Стены" });
        await Assert.That(result.Filters.Family).IsEqualTo("Basic Wall");
        await Assert.That(result.Filters.Type).IsEqualTo("200 mm");
        await Assert.That(result.Filters.Level).IsEqualTo("03");
        await Assert.That(result.Filters.View).IsEqualTo("План 3");
        await Assert.That(result.Filters.Workset).IsEqualTo("АР");
        await Assert.That(result.Filters.Phase).IsEqualTo("Новая конструкция");
        await Assert.That(result.Filters.AreaScheme).IsEqualTo("СПП в ГНС");
        await Assert.That(result.Filters.Parameters.Select(filter => filter.Operator)).IsEquivalentTo(new[]
        {
            ParameterOperator.Equals,
            ParameterOperator.Contains,
            ParameterOperator.Greater,
            ParameterOperator.Less,
            ParameterOperator.Empty,
            ParameterOperator.NotEmpty,
            ParameterOperator.Exists
        });
        await Assert.That(result.Fields).IsEquivalentTo(new[] { "id", "category", "ADSK_Номер корпуса" });
        await Assert.That(result.Offset).IsEqualTo(20);
        await Assert.That(result.Limit).IsEqualTo(10);
        await Assert.That(result.Sort.Field).IsEqualTo("level");
        await Assert.That(result.Sort.Descending).IsTrue();
    }

    [Test]
    public async Task Parse_AggregateElements_ReadsSharedFiltersAndAggregation()
    {
        const string json = """
                            {
                              "command":"aggregate-elements",
                              "categories":["Зоны"],
                              "areaScheme":"СПП в ГНС",
                              "parameterFilters":[{"parameter":"Area","operator":"greater","value":"0"}],
                              "groupBy":["level","type"],
                              "numericField":"Area"
                            }
                            """;

        var result = ControlJobParser.Parse(json);

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.AggregateElements);
        await Assert.That(result.Filters.AreaScheme).IsEqualTo("СПП в ГНС");
        await Assert.That(result.GroupBy).IsEquivalentTo(new[] { "level", "type" });
        await Assert.That(result.NumericField).IsEqualTo("Area");
    }

    [Test]
    public async Task Parse_QueryElements_RejectsValueOperatorWithoutValue()
    {
        var result = ControlJobParser.Parse(
            "{\"command\":\"query-elements\",\"parameterFilters\":[{\"parameter\":\"Area\",\"operator\":\"greater\"}]}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.Invalid);
        await Assert.That(result.Error).Contains("нужно value");
    }

    [Test]
    public async Task Parse_CatalogWarningsAndRelations_ReadsCommandParameters()
    {
        var catalog = ControlJobParser.Parse("{\"command\":\"list-catalog\",\"section\":\"parameters\"}");
        var warnings = ControlJobParser.Parse(
            "{\"command\":\"list-warnings\",\"warningText\":\"Стены пересекаются\",\"includeElements\":true}");
        var relations = ControlJobParser.Parse(
            "{\"command\":\"list-relations\",\"relation\":\"group-elements\",\"sourceId\":42}");

        await Assert.That(catalog.Kind).IsEqualTo(ControlJobKind.ListCatalog);
        await Assert.That(catalog.CatalogSection).IsEqualTo("parameters");
        await Assert.That(warnings.Kind).IsEqualTo(ControlJobKind.ListWarnings);
        await Assert.That(warnings.WarningText).IsEqualTo("Стены пересекаются");
        await Assert.That(warnings.IncludeElements).IsTrue();
        await Assert.That(relations.Kind).IsEqualTo(ControlJobKind.ListRelations);
        await Assert.That(relations.SourceId).IsEqualTo(42L);
    }

    [Test]
    [Arguments("level-rooms", "\"sourceName\":\"01\"", null, "01")]
    [Arguments("group-elements", "\"sourceId\":42", 42L, null)]
    [Arguments("nested-family", "\"sourceId\":43", 43L, null)]
    [Arguments("area-scheme-elements", "\"sourceName\":\"АР\"", null, "АР")]
    [Arguments("view-template-dependents", "\"sourceName\":\"АР шаблон\"", null, "АР шаблон")]
    public async Task Parse_Relations_AcceptsEveryDocumentedKind(
        string relation,
        string sourceJson,
        long? expectedId,
        string? expectedName)
    {
        var result = ControlJobParser.Parse(
            $"{{\"command\":\"list-relations\",\"relation\":\"{relation}\",{sourceJson}}}");

        await Assert.That(result.Kind).IsEqualTo(ControlJobKind.ListRelations);
        await Assert.That(result.Relation).IsEqualTo(relation);
        await Assert.That(result.SourceId).IsEqualTo(expectedId);
        await Assert.That(result.SourceName).IsEqualTo(expectedName);
    }
}
