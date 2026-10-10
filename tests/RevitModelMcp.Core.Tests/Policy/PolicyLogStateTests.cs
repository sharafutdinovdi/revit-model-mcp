using RevitModelMcp.Core.Policy;

namespace RevitModelMcp.Core.Tests.Policy;

public sealed class PolicyLogStateTests
{
    [Test]
    public async Task UnreadableProblem_IsReportedOncePerDistinctProblem()
    {
        var state = new PolicyLogState();
        var first = state.NewMessages(MachinePolicy.FailClosed("a"));
        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(state.NewMessages(MachinePolicy.FailClosed("a")).Count).IsEqualTo(0);
        await Assert.That(state.NewMessages(MachinePolicy.FailClosed("b")).Count).IsEqualTo(1);
        await Assert.That(state.NewMessages(MachinePolicy.Empty).Count).IsEqualTo(0);
        await Assert.That(state.NewMessages(MachinePolicy.FailClosed("b")).Count).IsEqualTo(1);
    }

    [Test]
    public async Task IgnoredKeys_AreReportedOnceUntilTheyChange()
    {
        var state = new PolicyLogState();
        var policy = new MachinePolicy { IgnoredKeys = ["X"] };
        await Assert.That(state.NewMessages(policy).Single()).Contains("X");
        await Assert.That(state.NewMessages(policy).Count).IsEqualTo(0);
        await Assert.That(state.NewMessages(new MachinePolicy { IgnoredKeys = ["Y"] }).Count).IsEqualTo(1);
    }
}
