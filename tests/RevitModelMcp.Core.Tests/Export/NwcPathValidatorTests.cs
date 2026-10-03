using RevitModelMcp.Core.Export;

namespace RevitModelMcp.Core.Tests.Export;

public sealed class NwcPathValidatorTests
{
    [Test]
    [Arguments(@"C:\x\a.nwc")]
    [Arguments(@"\\srv\share\a.NWC")]
    public async Task Validate_AcceptsAbsoluteNwcPaths(string path)
    {
        NwcPathValidator.Validate(path, [@"\\srv\share"]);
        await Assert.That(path).IsNotEmpty();
    }

    [Test]
    [Arguments(@"a.nwc")]
    [Arguments(@"C:\x\a.nwd")]
    [Arguments(@"C:\x\..\a.nwc")]
    [Arguments(@"C:/a/../b.nwc")]
    [Arguments(@"\\?\C:\x\a.nwc")]
    [Arguments(@"\\?\UNC\srv\share\a.nwc")]
    [Arguments(@"//./C:/x/a.nwc")]
    [Arguments(@"\\srv\share\a.nwc")]
    [Arguments(@"C:\x\a?.nwc")]
    [Arguments(@"C:\x\.nwc")]
    [Arguments(@"C:\x\COM9.nwc")]
    [Arguments(@"C:\x\LPT5.report.nwc")]
    public async Task Validate_RejectsInvalidPaths(string path)
    {
        await Assert.That(() => NwcPathValidator.Validate(path)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Validate_TrustedUncRootAllowsOnlyThatShare()
    {
        NwcPathValidator.Validate(@"\\SRV\Share\a.nwc", [@"\\srv\share"]);
        NwcPathValidator.Validate("//SRV/Share/a.nwc", [@"\\srv\share"]);
        await Assert.That(() => NwcPathValidator.Validate(@"\\srv\other\a.nwc", [@"\\srv\share"]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task EnsureSettingsXmlSize_RejectsOnlyFilesOverOneMiB()
    {
        NwcPathValidator.EnsureSettingsXmlSize(1024 * 1024);
        await Assert.That(() => NwcPathValidator.EnsureSettingsXmlSize(1024 * 1024 + 1))
            .Throws<ArgumentException>();
    }
}
