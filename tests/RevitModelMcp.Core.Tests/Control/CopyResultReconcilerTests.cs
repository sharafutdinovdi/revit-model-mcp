using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class CopyResultReconcilerTests
{
    [Test]
    public async Task Reconcile_DropsPhantomIdsAndKeepsOrder()
    {
        var (copies, dropped) = CopyResultReconciler.Reconcile([[1, 2, 3], [4, 5]], id => id is not (2 or 4));
        await Assert.That(dropped).IsEqualTo(2);
        await Assert.That(copies.Count).IsEqualTo(2);
        await Assert.That(copies[0]).IsEquivalentTo(new List<long> { 1, 3 });
        await Assert.That(copies[1]).IsEquivalentTo(new List<long> { 5 });
    }

    [Test]
    public async Task Reconcile_KeepsEmptyInnerLists()
    {
        var (copies, dropped) = CopyResultReconciler.Reconcile([[], [7]], _ => false);
        await Assert.That(dropped).IsEqualTo(1);
        await Assert.That(copies.Count).IsEqualTo(2);
        await Assert.That(copies[0]).IsEmpty();
        await Assert.That(copies[1]).IsEmpty();
    }

    [Test]
    public async Task Reconcile_NothingDropped_ReturnsSameIds()
    {
        var (copies, dropped) = CopyResultReconciler.Reconcile([[1, 2]], _ => true);
        await Assert.That(dropped).IsEqualTo(0);
        await Assert.That(copies[0]).IsEquivalentTo(new List<long> { 1, 2 });
    }

    [Test]
    [Arguments(1, "1 copy was removed")]
    [Arguments(3, "3 copies were removed")]
    public async Task Warning_UsesPlural(int dropped, string expected) =>
        await Assert.That(CopyResultReconciler.Warning(dropped)).Contains(expected);

    [Test]
    public async Task EmptyGroups_KeepsGroupShapeButNoIds()
    {
        var groups = CopyResultReconciler.EmptyGroups([[2661056, 2661057], [9]]);
        await Assert.That(groups.Count).IsEqualTo(2);
        await Assert.That(groups.SelectMany(group => group).Count()).IsEqualTo(0);
    }

    [Test]
    [Arguments("mirror", "Mirror created nothing in a.rvt")]
    [Arguments("copy", "Copy created nothing in a.rvt")]
    public async Task NothingCreatedSummary_SaysNothingWasCreated(string command, string expected) =>
        await Assert.That(CopyResultReconciler.NothingCreatedSummary(command, "a.rvt")).Contains(expected);
}
