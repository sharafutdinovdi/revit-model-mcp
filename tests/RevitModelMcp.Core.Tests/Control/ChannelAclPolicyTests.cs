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
    [Arguments("S-1-3-0")]
    [Arguments("S-1-3-4")]
    public async Task CreatorAndOwnerRightsRulesAreAllowed(string sid)
    {
        var entries = new[] { new ChannelAclEntry(sid, 0x10000000, true, true) };

        await Assert.That(ChannelAclPolicy.RefusalReason(CurrentUser, CurrentUser, entries)).IsNull();
    }

    [Test]
    [Arguments("S-1-3-0")]
    [Arguments("S-1-3-4")]
    public async Task CreatorAndOwnerRightsAreNotTrustedOwners(string sid)
    {
        await Assert.That(ChannelAclPolicy.RefusalReason(sid, CurrentUser, [])).Contains("owner");
    }

    [Test]
    [Arguments(true, false, CurrentUser, true)]
    [Arguments(true, true, CurrentUser, false)]
    [Arguments(false, false, CurrentUser, false)]
    [Arguments(true, false, OtherUser, false)]
    public async Task ExistingDirectoryProtectionDependsOnOwnerAndDacl(
        bool migrateOwnedDirectory, bool accessRulesProtected, string ownerSid, bool expected)
    {
        var shouldProtect = ChannelAclPolicy.ShouldProtectExistingDirectory(
            migrateOwnedDirectory, accessRulesProtected, ownerSid, CurrentUser);

        await Assert.That(shouldProtect).IsEqualTo(expected);
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
    [Test]
    public async Task RepairPlanRemovesInheritedVirtualizationPrincipalsWithoutLooseningRefusal()
    {
        const string capability = "S-1-15-3-1024-2268835264-3721307629-241982045-173645152-1490879176-104643441-2915960892-1612460704";
        var entries = new[]
        {
            new ChannelAclEntry(CurrentUser, 0x1F01FF, true, true),
            new ChannelAclEntry("S-1-5-18", 0x1F01FF, true, true),
            new ChannelAclEntry("S-1-5-32-544", 0x1F01FF, true, true),
            new ChannelAclEntry(capability, 0x1F01FF, true, true),
            new ChannelAclEntry("S-1-5-83-0", 0x6, true, true)
        };
        var plan = ChannelAclRepairPolicy.Plan("S-1-5-32-544", CurrentUser, false, entries);

        await Assert.That(plan.Required).IsTrue();
        await Assert.That(plan.OwnerSid).IsEqualTo("S-1-5-32-544");
        await Assert.That(plan.RemovedSids.Count).IsEqualTo(2);
        await Assert.That(plan.RemovedSids.Contains(capability)).IsTrue();
        await Assert.That(plan.RemovedSids.Contains("S-1-5-83-0")).IsTrue();
        await Assert.That(ChannelAclPolicy.RefusalReason(plan.OwnerSid, CurrentUser, entries)).IsNotNull();
        await Assert.That(ChannelAclPolicy.RefusalReason(CurrentUser, CurrentUser, plan.TargetEntries)).IsNull();
        await Assert.That(plan.TargetEntries.Count).IsEqualTo(3);
        await Assert.That(plan.TargetEntries.All(entry => entry.Allow && !entry.Inherited && entry.RightsMask == 0x1F01FF)).IsTrue();
    }

    [Test]
    public async Task PrivateFolderRepairPlanHasNothingToDo()
    {
        var target = ChannelAclRepairPolicy.Plan(CurrentUser, CurrentUser, true, []).TargetEntries;
        var plan = ChannelAclRepairPolicy.Plan(CurrentUser, CurrentUser, true, target);

        await Assert.That(plan.Required).IsFalse();
        await Assert.That(plan.RemovedSids.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RepairReviewListsReadOnlyForeignPrincipalsAndDeduplicatesSids()
    {
        var plan = ChannelAclRepairPolicy.Plan(CurrentUser, CurrentUser, true,
        [
            new ChannelAclEntry(OtherUser, 0x20089, true, false),
            new ChannelAclEntry(OtherUser, 0x2, true, true)
        ]);

        await Assert.That(plan.Required).IsTrue();
        await Assert.That(plan.RemovedSids.Count).IsEqualTo(1);
        await Assert.That(plan.Confirmation("channel", "Administrators", plan.RemovedSids, [])).Contains(OtherUser);
        await Assert.That(plan.Confirmation("channel", "Administrators", plan.RemovedSids, [])).Contains("Current owner: Administrators");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(500)]
    public async Task RepairConfirmationBoundsExamplesAndDeduplicatesPrincipals(int count)
    {
        var plan = ChannelAclRepairPolicy.Plan(CurrentUser, CurrentUser, true, []);
        var paths = Enumerable.Range(0, count).Select(index => $"instances/object-{index}.json").ToArray();
        var principal = $"Other account ({OtherUser})";

        var text = plan.Confirmation("channel", $"Owner ({CurrentUser})", [principal, principal.ToLowerInvariant()], paths);

        await Assert.That(text).Contains("Folder: channel");
        await Assert.That(text).Contains($"Current owner: Owner ({CurrentUser})");
        await Assert.That(text).Contains($"Objects whose permissions will change: {count}\n");
        await Assert.That(text.Split('\n').Count(line => line.Equals(principal, StringComparison.OrdinalIgnoreCase))).IsEqualTo(1);
        await Assert.That(text.Split('\n').Count(line => line.StartsWith("instances/object-", StringComparison.Ordinal))).IsEqualTo(Math.Min(5, count));
        foreach (var path in paths.Take(5)) await Assert.That(text).Contains(path);
        await Assert.That(text.Contains("and 495 more", StringComparison.Ordinal)).IsEqualTo(count == 500);
        await Assert.That(text.Contains("and 0 more", StringComparison.Ordinal)).IsFalse();
        await Assert.That(text).Contains("Continue?");
    }

    [Test]
    public async Task ManualRepairQuotesFolderAndExplainsRequiredRights()
    {
        var commands = ChannelAclRepairPolicy.ManualCommands("channel's folder", ["S-1-5-83-0"]);

        await Assert.That(commands).Contains("'channel''s folder'");
        await Assert.That(commands).Contains("/inheritance:r");
        await Assert.That(commands).Contains("/setowner");
        await Assert.That(commands).Contains("/remove '*S-1-5-83-0'");
        await Assert.That(ChannelAclRepairPolicy.ManualInstructions).Contains("WRITE_DAC");
        await Assert.That(ChannelAclRepairPolicy.ManualInstructions).Contains("WRITE_OWNER");
    }
}
