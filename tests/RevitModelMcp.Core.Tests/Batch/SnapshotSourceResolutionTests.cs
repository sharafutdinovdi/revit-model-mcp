using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class SnapshotSourceResolutionTests
{
    [Test]
    public async Task Resolve_InteractiveSourceUsesDocumentPath()
    {
        var source = SnapshotSourceResolution.Resolve(@"\\server\models\a.rvt", false);
        await Assert.That(source.Path).IsEqualTo(@"\\server\models\a.rvt");
        await Assert.That(source.Kind).IsEqualTo("unc");
        await Assert.That(source.SavedInYear).IsNull();
        await Assert.That(source.UpgradedInMemory).IsNull();
        await Assert.That(source.SkipReason).IsNull();
    }

    [Test]
    public async Task Resolve_DetachedInteractiveSourceSkipsFile()
    {
        var source = SnapshotSourceResolution.Resolve("unusable detached path", true);
        await Assert.That(source.Path).IsNull();
        await Assert.That(source.SkipReason).IsEqualTo("detached document has no source file");
    }

    [Test]
    public async Task Resolve_DetachedBatchSourceUsesSupervisorMetadata()
    {
        var source = SnapshotSourceResolution.Resolve("unusable detached path", true,
            "RSN://server/project/model.rvt", 2024, true);
        await Assert.That(source.Path).IsEqualTo("RSN://server/project/model.rvt");
        await Assert.That(source.Kind).IsEqualTo("rsn");
        await Assert.That(source.SavedInYear).IsEqualTo(2024);
        await Assert.That(source.UpgradedInMemory).IsTrue();
        await Assert.That(source.SkipReason).IsNull();

        var localSource = SnapshotSourceResolution.Resolve("unusable detached path", true,
            @"C:\models\source.rvt", 2026, false);
        await Assert.That(localSource.Path).IsEqualTo(@"C:\models\source.rvt");
        await Assert.That(localSource.Kind).IsEqualTo("local");
        await Assert.That(localSource.SavedInYear).IsEqualTo(2026);
        await Assert.That(localSource.UpgradedInMemory).IsFalse();
    }
}
