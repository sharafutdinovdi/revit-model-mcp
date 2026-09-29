using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ChannelAclPolicyTests
{
    private const string CurrentUser = "S-1-5-21-100-200-300-1001";
    private const string OtherUser = "S-1-5-21-100-200-300-1002";

    [Test]
    [Arguments(CurrentUser)]
    [Arguments("S-1-5-18")]
    [Arguments("S-1-5-32-544")]
    public async Task TrustedOwnersAndWritersAreAllowed(string owner)
    {
        var entries = new[]
        {
            new ChannelAclEntry(CurrentUser, 0x1F01FF, true, false),
            new ChannelAclEntry("S-1-5-18", 0x1F01FF, true, false),
            new ChannelAclEntry("S-1-5-32-544", 0x1F01FF, true, true)
        };

        await Assert.That(ChannelAclPolicy.RefusalReason(owner, CurrentUser, entries)).IsNull();
    }

    [Test]
    public async Task OtherOwnerIsRefusedEvenWithoutAccessRules()
    {
        var reason = ChannelAclPolicy.RefusalReason(OtherUser, CurrentUser, []);

        await Assert.That(reason).Contains("owner");
    }

    [Test]
    public async Task MissingOwnerIsRefused()
    {
        await Assert.That(ChannelAclPolicy.RefusalReason(null, CurrentUser, [])).Contains("owner");
    }

    [Test]
    [Arguments(0x2)]
    [Arguments(0x4)]
    [Arguments(0x10)]
    [Arguments(0x40)]
    [Arguments(0x100)]
    [Arguments(0x10000)]
    [Arguments(0x40000)]
    [Arguments(0x80000)]
    [Arguments(0x10000000)]
    [Arguments(0x40000000)]
    public async Task OtherPrincipalWithAnyWriteRightIsRefused(int right)
    {
        var entries = new[] { new ChannelAclEntry(OtherUser, right, true, false) };

        await Assert.That(ChannelAclPolicy.RefusalReason(CurrentUser, CurrentUser, entries)).Contains(OtherUser);
    }

    [Test]
    public async Task InheritedAllowRuleIsRefused()
    {
        var entries = new[] { new ChannelAclEntry("S-1-1-0", 0x2, true, true) };

        await Assert.That(ChannelAclPolicy.RefusalReason(CurrentUser, CurrentUser, entries)).Contains("Write access");
    }

    [Test]
    public async Task ReadOnlyAndDenyRulesDoNotGrantWriteAccess()
    {
        var entries = new[]
        {
            new ChannelAclEntry(OtherUser, 0x20089, true, true),
            new ChannelAclEntry(OtherUser, 0x1F01FF, false, false)
        };

        await Assert.That(ChannelAclPolicy.RefusalReason(CurrentUser, CurrentUser, entries)).IsNull();
    }
}
