using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class DeletionPolicyTests
{
    [Test]
    public async Task Threshold_IsFiveHundred()
    {
        await Assert.That(DeletionPolicy.ConfirmationThreshold).IsEqualTo(500);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(500, false)]
    [Arguments(501, true)]
    [Arguments(10000, true)]
    public async Task RequiresConfirmation_OnlyAboveThreshold(int count, bool expected)
    {
        await Assert.That(DeletionPolicy.RequiresConfirmation(count)).IsEqualTo(expected);
    }

    [Test]
    public async Task BatchRefusal_MentionsCountAndThreshold()
    {
        var message = DeletionPolicy.BatchRefusal(742);
        await Assert.That(message).Contains("742");
        await Assert.That(message).Contains("500");
    }
}
