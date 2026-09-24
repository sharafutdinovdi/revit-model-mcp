using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Core.Tests.Activity;

public sealed class ChangeMergeTests
{
    [Test]
    public async Task AddedThenDeleted_CancelsOutOfBothLists()
    {
        var merge = new ChangeMerge();
        merge.Add(1);
        merge.Delete(1);
        await Assert.That(merge.KindOf(1)).IsEqualTo(ChangeKind.None);
        await Assert.That(merge.Ids(ChangeKind.Created)).IsEmpty();
        await Assert.That(merge.Ids(ChangeKind.Deleted)).IsEmpty();
    }

    [Test]
    public async Task ModifiedThenDeleted_ReportsOnlyAsDeleted()
    {
        var merge = new ChangeMerge();
        merge.Modify(1);
        merge.Delete(1);
        await Assert.That(merge.KindOf(1)).IsEqualTo(ChangeKind.Deleted);
        await Assert.That(merge.Ids(ChangeKind.Changed)).IsEmpty();
        await Assert.That(merge.Ids(ChangeKind.Deleted)).IsEquivalentTo([1L]);
    }

    [Test]
    public async Task AddedThenModified_StaysCreated()
    {
        var merge = new ChangeMerge();
        merge.Add(1);
        merge.Modify(1);
        await Assert.That(merge.KindOf(1)).IsEqualTo(ChangeKind.Created);
        await Assert.That(merge.Ids(ChangeKind.Changed)).IsEmpty();
    }

    [Test]
    public async Task DeletedThenReAdded_ReportsAsCreated()
    {
        var merge = new ChangeMerge();
        merge.Delete(1);
        merge.Add(1);
        await Assert.That(merge.KindOf(1)).IsEqualTo(ChangeKind.Created);
        await Assert.That(merge.Ids(ChangeKind.Deleted)).IsEmpty();
    }

    [Test]
    public async Task EachId_ReportedExactlyOnce()
    {
        var merge = new ChangeMerge();
        merge.Modify(1);
        merge.Modify(1);
        merge.Modify(1);
        await Assert.That(merge.Ids(ChangeKind.Changed)).IsEquivalentTo([1L]);
    }

    [Test]
    public async Task TransientCreateDeletePair_DoesNotHideUnrelatedChanges()
    {
        // A real align-link-datums job: one level is moved (Changed), one placeholder level Revit
        // creates and removes again while resolving missing levels (transient), and one obsolete
        // grid is genuinely deleted.
        var merge = new ChangeMerge();
        merge.Modify(10);
        merge.Add(20);
        merge.Delete(20);
        merge.Delete(30);

        await Assert.That(merge.Ids(ChangeKind.Changed)).IsEquivalentTo([10L]);
        await Assert.That(merge.Ids(ChangeKind.Created)).IsEmpty();
        await Assert.That(merge.Ids(ChangeKind.Deleted)).IsEquivalentTo([30L]);
        await Assert.That(merge.KindOf(20)).IsEqualTo(ChangeKind.None);
    }

    [Test]
    public async Task Ids_PreservesFirstSeenOrder()
    {
        var merge = new ChangeMerge();
        merge.Add(3);
        merge.Add(1);
        merge.Add(2);
        var ids = merge.Ids(ChangeKind.Created);
        await Assert.That(ids.Count).IsEqualTo(3);
        await Assert.That(ids[0]).IsEqualTo(3L);
        await Assert.That(ids[1]).IsEqualTo(1L);
        await Assert.That(ids[2]).IsEqualTo(2L);
    }
}
