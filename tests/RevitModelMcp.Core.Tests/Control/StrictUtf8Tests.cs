using System.Text;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class StrictUtf8Tests
{
    [Test]
    public async Task ValidUtf8IsDecoded()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"view\":\"План 1\"}");
        await Assert.That(StrictUtf8.TryDecode(bytes, out var text)).IsTrue();
        await Assert.That(text).IsEqualTo("{\"view\":\"План 1\"}");
    }

    [Test]
    [Arguments(new byte[] { 0x7B, 0xFF, 0x7D })]
    [Arguments(new byte[] { 0xC3 })]
    [Arguments(new byte[] { 0xED, 0xA0, 0x80 })]
    public async Task MalformedUtf8IsRejected(byte[] bytes)
    {
        await Assert.That(StrictUtf8.TryDecode(bytes, out var text)).IsFalse();
        await Assert.That(text).IsEqualTo(string.Empty);
    }
}
