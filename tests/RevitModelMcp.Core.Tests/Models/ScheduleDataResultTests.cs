using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Models;

public sealed class ScheduleDataResultTests
{
    [Test]
    [Arguments(1, 0, 16, 1)]
    [Arguments(1, 15, 16, 16)]
    [Arguments(1, 16, 16, 17)]
    [Arguments(1, int.MaxValue - 1, 16, 17)]
    [Arguments(1, int.MaxValue, 16, 17)]
    [Arguments(3, 0, 19, 3)]
    [Arguments(3, 16, 19, 19)]
    [Arguments(3, 17, 19, 20)]
    [Arguments(3, int.MaxValue, 19, 20)]
    public async Task FirstRow_NeverOverflowsAndLandsPastTheEndWhenOffsetIsBeyondTheData(int dataStart, int offset, int lastRow, int expected) =>
        await Assert.That(ScheduleDataResult.FirstRow(dataStart, offset, lastRow)).IsEqualTo(expected);

    [Test]
    [Arguments(0, 5, 16, true)]
    [Arguments(11, 5, 16, false)]
    [Arguments(int.MaxValue, 0, 16, false)]
    [Arguments(int.MaxValue - 1, 5, 16, false)]
    public async Task IsTruncated_UsesWideArithmetic(int offset, int rowCount, int total, bool expected) =>
        await Assert.That(ScheduleDataResult.IsTruncated(offset, rowCount, total)).IsEqualTo(expected);
}
