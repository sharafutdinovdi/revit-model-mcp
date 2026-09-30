using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ParameterResolutionTests
{
    private static readonly ParameterCandidate[] Candidates =
    [
        new("ALL_MODEL_MARK", "Марка", "String", "instance", "built-in", "ALL_MODEL_MARK"),
        new("f5257291-6b0b-4ef4-a9a1-b5aa937127a4", "Code", "Integer", "type", "shared", null),
        new("321", "Comments", "Double", "instance", "project", null)
    ];

    [Test]
    public async Task Resolve_AcceptsLocalizedBuiltInAndEnglishNames()
    {
        await Assert.That(ParameterResolution.Resolve("Марка", null, Candidates).Id).IsEqualTo("ALL_MODEL_MARK");
        await Assert.That(ParameterResolution.Resolve("ALL_MODEL_MARK", null, Candidates).Id).IsEqualTo("ALL_MODEL_MARK");
        await Assert.That(ParameterResolution.Resolve("Mark", null, Candidates).Id).IsEqualTo("ALL_MODEL_MARK");
    }

    [Test]
    public async Task Resolve_ExplicitIdentifierIgnoresNameAndHasNoFallback()
    {
        await Assert.That(ParameterResolution.Resolve("Wrong", "321", Candidates).Name).IsEqualTo("Comments");
        await Assert.That(ParameterResolution.Resolve("Wrong", "F5257291-6B0B-4EF4-A9A1-B5AA937127A4", Candidates).Kind).IsEqualTo("shared");
        var error = Assert.Throws<ArgumentException>(() => ParameterResolution.Resolve("Марка", "999", Candidates));
        await Assert.That(error.Message).Contains("999");
    }

    [Test]
    public async Task Resolve_SharedParameterAcceptsGuidAndParameterElementId()
    {
        var shared = new ParameterCandidate("f5257291-6b0b-4ef4-a9a1-b5aa937127a4", "Code", "Integer", "type", "shared", null, 456);
        var candidates = new[] { shared };

        await Assert.That(ParameterResolution.Resolve("Wrong", shared.Id, candidates)).IsEqualTo(shared);
        await Assert.That(ParameterResolution.Resolve("Wrong", "456", candidates)).IsEqualTo(shared);
    }

    [Test]
    public async Task Resolve_ListsEverySameNameCandidateInStableOrder()
    {
        var candidates = new[]
        {
            new ParameterCandidate("321", "Mark", "Double", "type", "project", null),
            Candidates[0],
            new ParameterCandidate("f5257291-6b0b-4ef4-a9a1-b5aa937127a4", "Mark", "Integer", "instance", "shared", null)
        };
        var error = Assert.Throws<ArgumentException>(() => ParameterResolution.Resolve("Mark", null, candidates));
        await Assert.That(error.Message).IsEqualTo("Parameter 'Mark' is ambiguous: " +
            "id=ALL_MODEL_MARK, name=Марка, storageType=String, owner=instance, kind=built-in; " +
            "id=f5257291-6b0b-4ef4-a9a1-b5aa937127a4, name=Mark, storageType=Integer, owner=instance, kind=shared; " +
            "id=321, name=Mark, storageType=Double, owner=type, kind=project");
    }

    [Test]
    public async Task ValidateValue_RequiresMatchingJsonType()
    {
        await Assert.That(ParameterResolution.ValidateValue("Code", "String", "")).IsEqualTo("");
        await Assert.That(ParameterResolution.ValidateValue("Code", "Integer", 2)).IsEqualTo(2);
        await Assert.That(ParameterResolution.ValidateValue("Code", "Double", 2)).IsEqualTo(2.0);
        await Assert.That(ParameterResolution.ValidateValue("Code", "Double", 2.5)).IsEqualTo(2.5);
        foreach (var (storage, value) in new (string, object)[] { ("String", 1), ("Integer", "1"), ("Double", "2.5") })
        {
            var error = Assert.Throws<ArgumentException>(() => ParameterResolution.ValidateValue("Code", storage, value));
            await Assert.That(error.Message).Contains("Code");
            await Assert.That(error.Message).Contains(storage);
        }
    }
}
