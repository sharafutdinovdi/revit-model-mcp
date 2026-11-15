using System.Globalization;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Core.Tests.Query;

public sealed class QueryParameterFilterTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FilterResolution_RefusesUnknownNamesWithTheOriginalMessage(bool hasCandidates)
    {
        var original = Assert.Throws<ArgumentException>(() => QueryParameterValidator.Resolve("Lenght", ["Length"]));
        var exception = Assert.Throws<ArgumentException>(() =>
            QueryParameterValidator.ValidateFilterResolution("Lenght", hasCandidates, false, () => ["Length"]));
        await Assert.That(exception.Message).IsEqualTo(original.Message);
        await Assert.That(exception.ParamName).IsEqualTo(original.ParamName);
    }

    [Test]
    public async Task FilterResolution_RefusesKnownNamesAbsentFromNonemptyCandidates()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            QueryParameterValidator.ValidateFilterResolution("Width", true, false, () => ["Width"]));
        await Assert.That(exception.Message).StartsWith(
            "Parameter 'Width' was not found on the queried elements or their types. Use list-catalog with section=parameters to see available names.");
        await Assert.That(exception.ParamName).IsEqualTo("requested");
    }

    [Test]
    public async Task FilterResolution_AllowsKnownNamesWithEmptyCandidates()
    {
        var validations = 0;
        QueryParameterValidator.ValidateFilterResolution("Width", false, false, () =>
        {
            validations++;
            return ["Width"];
        });
        await Assert.That(validations).IsEqualTo(1);
    }

    [Test]
    public void FilterResolution_AllowsNamesResolvedOnAnyQueriedCategory()
    {
        QueryParameterValidator.ValidateFilterResolution("Width", true, true,
            () => throw new InvalidOperationException("A resolved filter does not need model-wide validation."));
    }

    [Test]
    [Arguments("CURVE_ELEM_LENGTH", true)]
    [Arguments("curve_elem_length", true)]
    [Arguments("ALTERNATIVE_LENGTH", true)]
    [Arguments("Length", false)]
    [Arguments("123456", false)]
    [Arguments("-1004005", false)]
    [Arguments("+123456", false)]
    public async Task BuiltInAlias_AcceptsOnlyDefinedNames(string requested, bool expected)
    {
        var aliases = new HashSet<string>(["CURVE_ELEM_LENGTH", "ALTERNATIVE_LENGTH"], StringComparer.OrdinalIgnoreCase);
        await Assert.That(QueryParameterSemantics.IsDefinedAlias(requested, aliases)).IsEqualTo(expected);
    }

    [Test]
    public async Task NamedSources_UsesAllMatchingInstanceParametersBeforeTypeParameters()
    {
        var instance = new[] { (Name: "Length", Id: -1004005), (Name: "Length", Id: 42), (Name: "Width", Id: 43) };
        var typeReads = 0;
        var result = QueryParameterSemantics.ResolveNamedSources(instance, () =>
        {
            typeReads++;
            return new[] { (Name: "Length", Id: 99) };
        }, parameter => parameter.Name == "Length");
        await Assert.That(result.Select(parameter => parameter.Id).ToArray()).IsEquivalentTo(new[] { -1004005, 42 });
        await Assert.That(typeReads).IsEqualTo(0);
    }

    [Test]
    public async Task NamedSources_FallsBackToAllMatchingTypeParameters()
    {
        var result = QueryParameterSemantics.ResolveNamedSources(
            new[] { (Name: "Width", Id: 1) },
            () => new[] { (Name: "Length", Id: 2), (Name: "Length", Id: 3) },
            parameter => parameter.Name == "Length");
        await Assert.That(result.Select(parameter => parameter.Id).ToArray()).IsEquivalentTo(new[] { 2, 3 });
    }

    [Test]
    public async Task NamedSources_DoesNotResolveANameOnlyPresentOnUnrelatedElements()
    {
        var result = QueryParameterSemantics.ResolveNamedSources(
            new[] { (Name: "Width", Id: 1) },
            () => new[] { (Name: "Height", Id: 2) },
            parameter => parameter.Name == "Length");
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("Length", "Length", -1004005L, null, true)]
    [Arguments("length", "Length", -1004005L, null, true)]
    [Arguments("Length", "Length", 42L, null, true)]
    [Arguments("Length", "Width", 42L, null, false)]
    [Arguments("CURVE_ELEM_LENGTH", "Length", -1004005L, -1004005L, true)]
    [Arguments("CURVE_ELEM_LENGTH", "Length", 42L, -1004005L, false)]
    public async Task ParameterName_UsesActualLabelsAndExplicitBuiltInIds(
        string requested, string name, long id, long? builtInId, bool expected)
    {
        await Assert.That(QueryParameterSemantics.MatchesParameterName(requested, name, id, builtInId)).IsEqualTo(expected);
    }

    [Test]
    public async Task FilterAlternatives_PreservesUniqueRuleWithoutWrapping()
    {
        var rule = new object();
        var result = QueryParameterSemantics.CombineFilterAlternatives(new[] { rule },
            _ => throw new InvalidOperationException("A unique rule must not be combined."));
        await Assert.That(ReferenceEquals(result, rule)).IsTrue();
    }

    [Test]
    [Arguments(ParameterOperator.Equals)]
    [Arguments(ParameterOperator.Greater)]
    [Arguments(ParameterOperator.Less)]
    [Arguments(ParameterOperator.Contains)]
    [Arguments(ParameterOperator.Exists)]
    [Arguments(ParameterOperator.Empty)]
    public async Task FilterAlternatives_CombinesAllResolvedIdsAsAlternatives(ParameterOperator operation)
    {
        var rules = new[] { $"{operation}:-1004005", $"{operation}:42" };
        var result = QueryParameterSemantics.CombineFilterAlternatives(rules,
            alternatives => string.Join(" OR ", alternatives));
        await Assert.That(result).IsEqualTo($"{operation}:-1004005 OR {operation}:42");
    }

    [Test]
    public void FilterAlternatives_RejectsUnresolvedNames()
    {
        Assert.Throws<ArgumentException>(() =>
            QueryParameterSemantics.CombineFilterAlternatives(Array.Empty<string>(), _ => "unfiltered"));
    }

    [Test]
    [Arguments(ParameterOperator.Empty, false, false)]
    [Arguments(ParameterOperator.Empty, true, true)]
    [Arguments(ParameterOperator.NotEmpty, false, true)]
    [Arguments(ParameterOperator.NotEmpty, true, false)]
    [Arguments(ParameterOperator.Exists, false, true)]
    [Arguments(ParameterOperator.Exists, true, true)]
    public async Task ValueState_IgnoresIdsNotCarriedByTheElement(
        ParameterOperator operation, bool empty, bool expected)
    {
        await Assert.That(QueryParameterSemantics.MatchesValueState(new bool?[] { null, !empty }, operation))
            .IsEqualTo(expected);
    }

    [Test]
    [Arguments(ParameterOperator.Empty)]
    [Arguments(ParameterOperator.NotEmpty)]
    [Arguments(ParameterOperator.Exists)]
    public async Task ValueState_DoesNotMatchOnlyAbsentParameters(ParameterOperator operation)
    {
        await Assert.That(QueryParameterSemantics.MatchesValueState(new bool?[] { null, null }, operation)).IsFalse();
    }

    [Test]
    [Arguments("1000.5")]
    [Arguments("1000,5")]
    public async Task NumericThreshold_PreservesInvariantParsingWithDecimalCommaCulture(string threshold)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            await Assert.That(QueryParameterSemantics.ParseNumericFilterValue(threshold, "Length")).IsEqualTo(1000.5);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
