using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ChannelDirectoryPolicyTests
{
    [Test]
    [Arguments(null, 1, ChannelDirectoryAction.Keep)]
    [Arguments("acl refused", 1, ChannelDirectoryAction.Replace)]
    [Arguments(null, -6, ChannelDirectoryAction.Replace)]
    [Arguments(null, -2, ChannelDirectoryAction.Keep)]
    [Arguments(null, -5, ChannelDirectoryAction.Keep)]
    public async Task OwnDirectoryDecisionUsesAclAndCreationTolerance(
        string? refusalReason, int creationOffsetSeconds, ChannelDirectoryAction expected)
    {
        var processStartedUtc = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        var action = ChannelDirectoryPolicy.DecideOwnDirectory(refusalReason,
            processStartedUtc.AddSeconds(creationOffsetSeconds), processStartedUtc);

        await Assert.That(action).IsEqualTo(expected);
    }

    [Test]
    [Arguments("123", 123, false)]
    [Arguments("123.stale-ab12EF", 123, true)]
    [Arguments("0", 0, false)]
    [Arguments("2147483647", int.MaxValue, false)]
    public async Task InstanceDirectoryNamesAreParsed(string name, int expectedProcessId, bool expectedAside)
    {
        var parsed = ChannelDirectoryPolicy.TryParseInstanceDirectoryName(name, out var processId, out var isAside);

        await Assert.That(parsed).IsTrue();
        await Assert.That(processId).IsEqualTo(expectedProcessId);
        await Assert.That(isAside).IsEqualTo(expectedAside);
    }

    [Test]
    [Arguments("abc")]
    [Arguments("12.bak")]
    [Arguments("-5")]
    [Arguments("")]
    [Arguments("2147483648")]
    [Arguments("12.stale-")]
    [Arguments("12.stale-xyz")]
    [Arguments(".stale-ab12")]
    [Arguments("12.stale-ab.stale-cd")]
    [Arguments(" 12")]
    [Arguments("+12")]
    public async Task InvalidInstanceDirectoryNamesAreRejected(string name)
    {
        var parsed = ChannelDirectoryPolicy.TryParseInstanceDirectoryName(name, out _, out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    [Arguments("123", false, 15, true)]
    [Arguments("123", false, 13, false)]
    [Arguments("123", false, 14, false)]
    [Arguments("123", true, 15, false)]
    [Arguments("456", false, 15, false)]
    [Arguments("123.stale-ab12", true, 15, true)]
    [Arguments("456.stale-ab12", true, 15, true)]
    [Arguments("123.stale-ab12", false, 13, false)]
    [Arguments("abc", false, 15, false)]
    public async Task PruningPreservesActiveAndRecentDirectories(
        string name, bool isProcessAlive, int ageDays, bool expected)
    {
        var nowUtc = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        var shouldPrune = ChannelDirectoryPolicy.ShouldPrune(name, 456, isProcessAlive,
            nowUtc.AddDays(-ageDays), nowUtc, ChannelDirectoryPolicy.DefaultRetention);

        await Assert.That(shouldPrune).IsEqualTo(expected);
    }

    [Test]
    public async Task AsideNameUsesTheInstanceDirectoryFormat()
    {
        await Assert.That(ChannelDirectoryPolicy.AsideName(123, "ab12")).IsEqualTo("123.stale-ab12");
    }
}
