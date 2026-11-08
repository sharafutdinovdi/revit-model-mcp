using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ShowViewPolicyTests
{
    [Test]
    public async Task ChoosesMatchingLevelInsteadOfUnrelatedOpenPlanAsync()
    {
        ShowViewCandidate[] candidates =
        [
            new(1, 99, true, false, false, false, true, true),
            new(2, 10, true, false, false, false, false, false),
            new(3, null, false, true, false, false, false, true)
        ];
        await Assert.That(ShowViewPolicy.Choose(candidates, [10])!.Id).IsEqualTo(2L);
    }

    [Test]
    public async Task PrefersAlreadyOpenMatchingPlanAsync()
    {
        ShowViewCandidate[] candidates =
        [
            new(1, 10, true, false, false, false, false, false),
            new(2, 10, true, false, false, false, false, true)
        ];
        await Assert.That(ShowViewPolicy.Choose(candidates, [10, 10])!.Id).IsEqualTo(2L);
    }

    [Test]
    public async Task ActivatesOpenPlanEvenWhenAnotherViewIsActiveAsync()
    {
        var candidate = new ShowViewCandidate(2, 10, true, false, false, false, false, true);
        var result = ShowViewPolicy.Choose([candidate], [10]);
        await Assert.That(result!.IsActive).IsFalse();
        await Assert.That(result.Id).IsEqualTo(2L);
    }

    [Test]
    [Arguments(20)]
    [Arguments(-1)]
    public async Task MixedOrMissingLevelsRequireOrthographicThreeDimensionalViewAsync(long secondLevel)
    {
        ShowViewCandidate[] candidates =
        [
            new(1, 10, true, false, false, false, true, true),
            new(2, null, false, true, false, true, false, true),
            new(3, null, false, true, true, false, false, true),
            new(4, null, false, true, false, false, false, false)
        ];
        await Assert.That(ShowViewPolicy.Choose(candidates, [10, secondLevel])!.Id).IsEqualTo(4L);
    }

    [Test]
    public async Task NoSuitableViewReturnsNullAsync() =>
        await Assert.That(ShowViewPolicy.Choose([new(1, 99, true, false, false, false, false, true)], [10]) is null).IsTrue();
}
