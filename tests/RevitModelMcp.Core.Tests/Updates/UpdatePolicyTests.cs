using System.Text;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Updates;

namespace RevitModelMcp.Core.Tests.Updates;

public sealed class UpdatePolicyTests
{
    [Test]
    public async Task BuildsReleaseUrlFromTagWithVersionFallback()
    {
        const string releasePage = "https://github.com/sharafutdinovdi/revit-model-mcp/releases";
        await Assert.That(UpdatePolicy.BuildReleaseUrl(releasePage, "v0.6.98", "0.6.98"))
            .IsEqualTo($"{releasePage}/tag/v0.6.98");
        await Assert.That(UpdatePolicy.BuildReleaseUrl(releasePage, null, "0.6.98"))
            .IsEqualTo($"{releasePage}/tag/v0.6.98");
    }

    [Test]
    public async Task SelectsOnlyNewStableVersions()
    {
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0", false, false)).IsTrue();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0-rc.1", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0", true, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0", false, true)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.4.0", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.5.0-rc.1+abc", "v0.5.0", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.5.0-rc.1", "v0.5.1", false, false)).IsTrue();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0/../payload", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0.1", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "V0.5.0", false, false)).IsFalse();
    }

    [Test]
    public async Task DevAndPrereleaseBuildsAreNeverReplacedBySameOrOlderRelease()
    {
        await Assert.That(UpdatePolicy.IsNewerStable("0.9.1-dev+d51f403", "v0.9.1", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.9.1-dev", "v0.9.1", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.9.2-dev", "v0.9.1", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.9.1+abc", "v0.9.1", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.9.1-dev", "v0.9.2", false, false)).IsTrue();
    }

    [Test]
    public async Task ThrottlesForTwentyFourHours()
    {
        var now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        await Assert.That(UpdatePolicy.ShouldCheck(now, null)).IsTrue();
        await Assert.That(UpdatePolicy.ShouldCheck(now, now.AddHours(-23))).IsFalse();
        await Assert.That(UpdatePolicy.ShouldCheck(now, now.AddHours(-24))).IsTrue();
    }

    private static UpdateInputs Release(string? policy = null, string? machine = null, string? user = null,
        bool? machineCheck = null, bool? userCheck = null, string? env = null, bool perUser = true,
        UpdateChannel channel = UpdateChannel.Release) =>
        new(channel, perUser, policy, machine, user, machineCheck, userCheck, env);

    [Test]
    public async Task DefaultsToNotifyForReleaseAndOffForDev()
    {
        await Assert.That(UpdateDecision.Decide(Release())).IsEqualTo(UpdateMode.Notify);
        await Assert.That(UpdateDecision.Decide(Release(channel: UpdateChannel.Dev))).IsEqualTo(UpdateMode.Off);
    }

    [Test]
    public async Task StoreAndManagedChannelsNeverUpdate()
    {
        foreach (var channel in new[] { UpdateChannel.Store, UpdateChannel.Managed })
            await Assert.That(UpdateDecision.Decide(Release(policy: "auto", user: "auto", channel: channel))).IsEqualTo(UpdateMode.Off);
    }

    [Test]
    public async Task PolicyBeatsSettingsAndCannotBeRaisedByUser()
    {
        await Assert.That(UpdateDecision.Decide(Release(policy: "off", user: "auto"))).IsEqualTo(UpdateMode.Off);
        await Assert.That(UpdateDecision.Decide(Release(policy: "notify", user: "auto", machine: "auto"))).IsEqualTo(UpdateMode.Notify);
        await Assert.That(UpdateDecision.Decide(Release(policy: "auto"))).IsEqualTo(UpdateMode.Auto);
    }

    [Test]
    public async Task UserCanRaiseChannelDefaultButMachineSettingsCapUser()
    {
        await Assert.That(UpdateDecision.Decide(Release(user: "auto"))).IsEqualTo(UpdateMode.Auto);
        await Assert.That(UpdateDecision.Decide(Release(user: "off"))).IsEqualTo(UpdateMode.Off);
        await Assert.That(UpdateDecision.Decide(Release(machine: "notify", user: "auto"))).IsEqualTo(UpdateMode.Notify);
        await Assert.That(UpdateDecision.Decide(Release(machine: "auto"))).IsEqualTo(UpdateMode.Auto);
    }

    [Test]
    public async Task LegacyOptOutsAndEnvironmentLowerToOff()
    {
        await Assert.That(UpdateDecision.Decide(Release(machineCheck: false))).IsEqualTo(UpdateMode.Off);
        await Assert.That(UpdateDecision.Decide(Release(userCheck: false, user: "auto"))).IsEqualTo(UpdateMode.Off);
        await Assert.That(UpdateDecision.Decide(Release(machineCheck: true, userCheck: true))).IsEqualTo(UpdateMode.Notify);
        await Assert.That(UpdateDecision.Decide(Release(policy: "auto", env: "1"))).IsEqualTo(UpdateMode.Off);
        await Assert.That(UpdateDecision.Decide(Release(env: "0"))).IsEqualTo(UpdateMode.Notify);
        await Assert.That(UpdateDecision.Decide(Release(env: "true"))).IsEqualTo(UpdateMode.Notify);
    }

    [Test]
    public async Task PerMachineAndDevNeverAutoInstall()
    {
        await Assert.That(UpdateDecision.Decide(Release(policy: "auto", perUser: false))).IsEqualTo(UpdateMode.Notify);
        await Assert.That(UpdateDecision.Decide(Release(policy: "auto", channel: UpdateChannel.Dev))).IsEqualTo(UpdateMode.Notify);
        await Assert.That(UpdateDecision.Decide(Release(user: "auto", channel: UpdateChannel.Dev))).IsEqualTo(UpdateMode.Notify);
    }

    [Test]
    public async Task UnknownModeValuesFailClosed()
    {
        await Assert.That(UpdateDecision.Decide(Release(policy: "yes"))).IsEqualTo(UpdateMode.Off);
        await Assert.That(UpdateDecision.Decide(Release(user: "always"))).IsEqualTo(UpdateMode.Off);
        await Assert.That(UpdateDecision.Decide(Release(policy: " "))).IsEqualTo(UpdateMode.Notify);
        await Assert.That(UpdateDecision.ParseMode("AUTO")).IsEqualTo(UpdateMode.Auto);
    }

    [Test]
    public async Task ResolvesChannelFromMetadataAliasAndVersion()
    {
        await Assert.That(UpdateDecision.ResolveChannel(null, false, "0.9.1")).IsEqualTo(UpdateChannel.Release);
        await Assert.That(UpdateDecision.ResolveChannel("release", true, "0.9.1")).IsEqualTo(UpdateChannel.Managed);
        await Assert.That(UpdateDecision.ResolveChannel("release", false, "0.9.1-dev+abc")).IsEqualTo(UpdateChannel.Dev);
        await Assert.That(UpdateDecision.ResolveChannel("store", true, "0.9.1")).IsEqualTo(UpdateChannel.Store);
        await Assert.That(UpdateDecision.ResolveChannel("dev", false, "0.9.1")).IsEqualTo(UpdateChannel.Dev);
        await Assert.That(UpdateDecision.ResolveChannel("bogus", false, "0.9.1")).IsEqualTo(UpdateChannel.Managed);
    }

    [Test]
    public async Task DownloadUrlsMustStayOnTheFeedHost()
    {
        var github = new Uri("https://api.github.com");
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("https://github.com/o/r/releases/download/v1/a.msi", github)).IsTrue();
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("https://release-assets.githubusercontent.com/x", github)).IsTrue();
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("https://evil.example/a.msi", github)).IsFalse();
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("http://github.com/a.msi", github)).IsFalse();
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("https://user:pw@github.com/a.msi", github)).IsFalse();
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("http://127.0.0.1:9000/a.msi", github)).IsFalse();
        var mirror = new Uri("https://mirror.corp.example");
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("https://mirror.corp.example/a.msi", mirror)).IsTrue();
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("https://github.com/a.msi", mirror)).IsFalse();
        var loopback = new Uri("http://127.0.0.1:9000");
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("http://127.0.0.1:9000/a.msi", loopback)).IsTrue();
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl("http://127.0.0.1:9001/a.msi", loopback)).IsFalse();
        await Assert.That(UpdatePolicy.IsAllowedDownloadUrl(null, loopback)).IsFalse();
    }

    [Test]
    public async Task BlockedVersionsExpire()
    {
        var now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var blocked = new Dictionary<string, DateTimeOffset> { ["0.9.2"] = now + UpdatePolicy.BlockDuration };
        await Assert.That(UpdatePolicy.IsBlocked("v0.9.2", blocked, now)).IsTrue();
        await Assert.That(UpdatePolicy.IsBlocked("0.9.2", blocked, now.AddDays(8))).IsFalse();
        await Assert.That(UpdatePolicy.IsBlocked("0.9.3", blocked, now)).IsFalse();
    }

    [Test]
    public async Task SelectsExactAssetAndVerifiesPublishedChecksum()
    {
        const string fileName = "RevitModelMcp-0.5.0-SingleUser.msi";
        const string checksum = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        await Assert.That(UpdatePolicy.SelectSingleUserAsset([fileName, "RevitModelMcp-0.5.0-MultiUser.msi"], "v0.5.0")).IsEqualTo(fileName);
        await Assert.That(UpdatePolicy.SelectSingleUserAsset(["RevitModelMcp-0.5.0-MultiUser.msi"], "v0.5.0")).IsNull();
        var parsed = UpdatePolicy.ParseChecksum($"{checksum}  {fileName}\r\n", fileName);
        await Assert.That(parsed).IsEqualTo(checksum);
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        await Assert.That(UpdatePolicy.VerifyChecksum(valid, parsed!)).IsTrue();
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("changed"));
        await Assert.That(UpdatePolicy.VerifyChecksum(invalid, parsed!)).IsFalse();
    }

    [Test]
    public async Task ParsesTargetChecksumFromMultipleLfOnlyLines()
    {
        const string fileName = "RevitModelMcp-0.5.0-SingleUser.msi";
        const string checksum = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        var checksumText = $"{new string('a', 64)}  RevitModelMcp-0.5.0-MultiUser.msi\n"
                           + $"{checksum}  {fileName}\n"
                           + $"{new string('b', 64)}  RevitModelMcp-0.5.0.zip\n";
        var parsed = UpdatePolicy.ParseChecksum(checksumText, fileName);
        await Assert.That(parsed).IsEqualTo(checksum);
    }

    [Test]
    public async Task SystemNoticeUsesMessageAsActivityTitle()
    {
        var entry = new ActivityEntry { Command = "system-notice", Summary = "Update available: 0.5.0" };
        await Assert.That(ActivityTitleBuilder.Build(entry)).IsEqualTo("Update available: 0.5.0");
    }
}
