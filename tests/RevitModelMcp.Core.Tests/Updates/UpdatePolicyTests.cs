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
        await Assert.That(UpdatePolicy.IsNewerStable("0.5.0-rc.1+abc", "v0.5.0", false, false)).IsTrue();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0/../payload", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0.1", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "V0.5.0", false, false)).IsFalse();
    }

    [Test]
    public async Task ThrottlesForTwentyFourHours()
    {
        var now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        await Assert.That(UpdatePolicy.ShouldCheck(now, null)).IsTrue();
        await Assert.That(UpdatePolicy.ShouldCheck(now, now.AddHours(-23))).IsFalse();
        await Assert.That(UpdatePolicy.ShouldCheck(now, now.AddHours(-24))).IsTrue();
    }

    [Test]
    public async Task MachineAndUserOptOutPreventChecks()
    {
        await Assert.That(UpdatePolicy.IsEnabled(null, null, null)).IsTrue();
        await Assert.That(UpdatePolicy.IsEnabled(false, null, null)).IsFalse();
        await Assert.That(UpdatePolicy.IsEnabled(null, false, true)).IsFalse();
        await Assert.That(UpdatePolicy.IsEnabled(null, true, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsEnabled(null, true, true)).IsTrue();
    }

    [Test]
    public async Task ManagedInstallPreventsChecks()
    {
        await Assert.That(UpdatePolicy.IsEnabled(null, null, null, managedInstall: true)).IsFalse();
        await Assert.That(UpdatePolicy.IsEnabled(null, true, true, managedInstall: false)).IsTrue();
    }

    [Test]
    public async Task EnvironmentOptOutPreventsChecksOnlyForOne()
    {
        await Assert.That(UpdatePolicy.IsEnabled(null, null, null, environmentOptOut: "1")).IsFalse();
        await Assert.That(UpdatePolicy.IsEnabled(null, null, null, environmentOptOut: "0")).IsTrue();
        await Assert.That(UpdatePolicy.IsEnabled(null, null, null, environmentOptOut: null)).IsTrue();
        await Assert.That(UpdatePolicy.IsEnabled(null, null, null, environmentOptOut: "true")).IsTrue();
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
