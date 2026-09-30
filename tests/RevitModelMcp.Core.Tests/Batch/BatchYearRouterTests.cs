using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchYearRouterTests
{
    [Test]
    public async Task Route_SelectsExactThenNearestNewerNeverOlder()
    {
        var exact = BatchYearRouter.Route(2025, [2022, 2025, 2027]);
        var newer = BatchYearRouter.Route(2024, [2022, 2025, 2027]);
        var refused = BatchYearRouter.Route(2027, [2022, 2026]);
        await Assert.That(exact.RuntimeYear).IsEqualTo(2025);
        await Assert.That(exact.UpgradedInMemory).IsFalse();
        await Assert.That(newer.RuntimeYear).IsEqualTo(2025);
        await Assert.That(newer.UpgradedInMemory).IsTrue();
        await Assert.That(refused.RuntimeYear).IsNull();
    }

    [Test]
    public async Task Route_AllowedYearsRestrictInstalledSelection()
    {
        await Assert.That(BatchYearRouter.Route(2024, [2024, 2025], [2025]).RuntimeYear).IsEqualTo(2025);
        await Assert.That(BatchYearRouter.Parse("Autodesk Revit 2026")).IsEqualTo(2026);
        await Assert.That(() => BatchYearRouter.Parse("2019")).Throws<ArgumentException>();
    }

    [Test]
    public async Task GroupByRuntimeYear_ExcludesUnroutedModels()
    {
        var groups = BatchYearRouter.GroupByRuntimeYear([
            new BatchModel { Path = "a", RuntimeYear = 2025 },
            new BatchModel { Path = "b", RuntimeYear = 2025 },
            new BatchModel { Path = "c" }
        ]);
        await Assert.That(groups[2025].Count).IsEqualTo(2);
        await Assert.That(groups.Count).IsEqualTo(1);
    }
}
