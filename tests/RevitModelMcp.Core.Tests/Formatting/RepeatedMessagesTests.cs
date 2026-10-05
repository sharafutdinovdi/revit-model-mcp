using RevitModelMcp.Core.Formatting;

namespace RevitModelMcp.Core.Tests.Formatting;

public sealed class RepeatedMessagesTests
{
    [Test]
    public async Task Join_Empty_ReturnsEmptyString() =>
        await Assert.That(RepeatedMessages.Join([])).IsEqualTo("");

    [Test]
    public async Task Join_SingleMessage_IsUnchanged() =>
        await Assert.That(RepeatedMessages.Join(["msg"])).IsEqualTo("msg");

    [Test]
    public async Task Join_RepeatedMessage_AppendsCount() =>
        await Assert.That(RepeatedMessages.Join(["msg", "msg", "msg"])).IsEqualTo("msg (x3)");

    [Test]
    public async Task Join_MixedMessages_PreservesFirstSeenOrder() =>
        await Assert.That(RepeatedMessages.Join(["A", "B", "A"])).IsEqualTo("A (x2); B");
}
