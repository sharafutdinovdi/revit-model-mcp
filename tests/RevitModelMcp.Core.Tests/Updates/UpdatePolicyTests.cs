using System.Text;
using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Updates;

namespace RevitModelMcp.Core.Tests.Updates;

public sealed class UpdatePolicyTests
{
    [Test]
    public async Task SelectsOnlyNewStableVersions()
    {
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0", false, false)).IsTrue();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0-rc.1", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0", true, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.5.0", false, true)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.4.0", "v0.4.0", false, false)).IsFalse();
        await Assert.That(UpdatePolicy.IsNewerStable("0.5.0-rc.1+abc", "v0.5.0", false, false)).IsTrue();
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
    public async Task SystemNoticeUsesMessageAsActivityTitle()
    {
        var entry = new ActivityEntry { Command = "system-notice", Summary = "Update available: 0.5.0" };
        await Assert.That(ActivityTitleBuilder.Build(entry)).IsEqualTo("Update available: 0.5.0");
    }
}
