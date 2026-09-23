using RevitModelMcp.Core.Export;

namespace RevitModelMcp.Core.Tests.Export;

public sealed class NwcPathValidatorTests
{
    [Test]
    [Arguments(@"C:\x\a.nwc")]
    [Arguments(@"\\srv\share\a.NWC")]
    public async Task Validate_AcceptsAbsoluteNwcPaths(string path)
    {
        NwcPathValidator.Validate(path);
        await Assert.That(path).IsNotEmpty();
    }

    [Test]
    [Arguments(@"a.nwc")]
    [Arguments(@"C:\x\a.nwd")]
    [Arguments(@"C:\x\..\a.nwc")]
    [Arguments(@"\\?\C:\x\a.nwc")]
    [Arguments(@"C:\x\a?.nwc")]
    [Arguments(@"C:\x\.nwc")]
    public async Task Validate_RejectsInvalidPaths(string path)
    {
        await Assert.That(() => NwcPathValidator.Validate(path)).Throws<ArgumentException>();
    }
}
