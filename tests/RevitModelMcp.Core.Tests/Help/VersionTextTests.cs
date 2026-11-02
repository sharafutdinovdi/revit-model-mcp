using RevitModelMcp.Core.Help;

namespace RevitModelMcp.Core.Tests.Help;

public sealed class VersionTextTests
{
    [Test]
    [Arguments("1.0.0+657b705ce6bde2433973e836417a567b8854ed26", "1.0.0 (657b705)")]
    [Arguments("1.0.0", "1.0.0")]
    [Arguments("1.0.0+657b705", "1.0.0 (657b705)")]
    [Arguments("1.0.0+657b", "1.0.0 (657b)")]
    [Arguments("1.0.0+", "1.0.0")]
    public async Task Format_ShowsVersionWithShortSuffix(string informationalVersion, string expected)
    {
        await Assert.That(VersionText.Format(informationalVersion)).IsEqualTo(expected);
    }
}
