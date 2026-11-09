using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Query;

namespace RevitModelMcp.Core.Tests.Query;

public sealed class WorksharingCatalogTests
{
    [Test]
    public void NonWorkshared_RefusesBeforeReading()
    {
        Assert.Throws<InvalidOperationException>(() => WorksharingCatalog.ReadOwnership(false, [1],
            _ => throw new Exception("Must not read"), 0, 10, new SkippedReadDiagnostics()));
        Assert.Throws<InvalidOperationException>(() => WorksharingCatalog.RequireWorkshared(false));
    }

    [Test]
    public async Task Counts_AggregateAndMarkTheScanBound()
    {
        var diagnostics = new SkippedReadDiagnostics();
        var counts = WorksharingCatalog.CountElements([1, 2, 1], diagnostics);
        await Assert.That(counts[1]).IsEqualTo(2);
        await Assert.That(counts[2]).IsEqualTo(1);
        await Assert.That(diagnostics.Count).IsEqualTo(0);
        counts = WorksharingCatalog.CountElements(Enumerable.Repeat(1L, WorksharingCatalog.MaximumCountElements + 1), diagnostics);
        await Assert.That(counts[1]).IsEqualTo(WorksharingCatalog.MaximumCountElements);
        await Assert.That(diagnostics.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Worksets_PageAfterNameFilter()
    {
        var result = WorksharingCatalog.Page("worksets", [new() { Name = "A" }, new() { Name = "B" }, new() { Name = "b2" }], "b", 1, 1);
        await Assert.That(result.Total).IsEqualTo(2);
        await Assert.That(result.Items[0].Name).IsEqualTo("b2");
        await Assert.That(result.HasMore).IsFalse();
    }

    [Test]
    public async Task Ownership_PagesCandidatesAndReportsFailuresWithoutLosingContinuation()
    {
        var diagnostics = new SkippedReadDiagnostics();
        var reads = 0;
        var result = WorksharingCatalog.ReadOwnership(true, [1, 2, 3, 4, 5], id =>
        {
            reads++;
            if (id == 2) throw new InvalidOperationException("Unavailable owner");
            return id == 3 ? null : new CatalogItem { Id = id, Owner = "Owner", CheckoutStatus = "OwnedByOtherUser" };
        }, 1, 3, diagnostics);
        await Assert.That(reads).IsEqualTo(3);
        await Assert.That(result.Total).IsEqualTo(5);
        await Assert.That(result.NextOffset).IsEqualTo(4L);
        await Assert.That(result.HasMore).IsTrue();
        await Assert.That(result.Items.Single().Id).IsEqualTo(4L);
        await Assert.That(diagnostics.Items.Single().What).IsEqualTo("element 2 ownership");
        await Assert.That(diagnostics.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Ownership_CapsSkippedDetailsNotCount()
    {
        var diagnostics = new SkippedReadDiagnostics();
        var result = WorksharingCatalog.ReadOwnership(true, Enumerable.Range(1, 120).Select(id => (long)id),
            _ => throw new InvalidOperationException("Unavailable"), 0, 200, diagnostics);
        await Assert.That(diagnostics.Items.Count).IsEqualTo(100);
        await Assert.That(diagnostics.Count).IsEqualTo(120);
        await Assert.That(result.HasMore).IsFalse();
    }

    [Test]
    public async Task Parser_ValidatesOwnershipPaging()
    {
        var parsed = ControlJobParser.Parse("""{"command":"list-catalog","section":"ownership","offset":200,"limit":10}""");
        await Assert.That(parsed.Kind).IsEqualTo(ControlJobKind.ListCatalog);
        await Assert.That(parsed.Offset).IsEqualTo(200);
        var invalid = ControlJobParser.Parse("""{"command":"list-catalog","section":"ownership","offset":-1}""");
        await Assert.That(invalid.Kind).IsEqualTo(ControlJobKind.Invalid);
    }
}
