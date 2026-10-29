using RevitModelMcp.Core.Policy;
using RevitModelMcp.Core.Updates;

namespace RevitModelMcp.Core.Tests.Policy;

public sealed class CodeExecutionGateTests
{
    [Test]
    public async Task Store_IsOffByDefault()
    {
        var state = CodeExecutionGate.Evaluate(UpdateChannel.Store, MachinePolicy.Empty, null);
        await Assert.That(state.Enabled).IsFalse();
        await Assert.That(state.Source).IsEqualTo(CodeExecutionSource.ChannelDefault);
        await Assert.That(state.Value).IsEqualTo("off");
    }

    [Test]
    [Arguments("confirm")]
    [Arguments(" CONFIRM ")]
    public async Task Store_UserSettingIsIgnored(string setting)
    {
        var state = CodeExecutionGate.Evaluate(UpdateChannel.Store, MachinePolicy.Empty, setting);
        await Assert.That(state.Enabled).IsFalse();
        await Assert.That(state.Source).IsEqualTo(CodeExecutionSource.ChannelDefault);
        await Assert.That(CodeExecutionGate.NeedsUserSetting(UpdateChannel.Store, MachinePolicy.Empty)).IsFalse();
    }

    [Test]
    [Arguments("off")]
    [Arguments("on")]
    [Arguments("")]
    public async Task Store_OtherUserValuesKeepItOff(string setting)
    {
        await Assert.That(CodeExecutionGate.Evaluate(UpdateChannel.Store, MachinePolicy.Empty, setting).Enabled).IsFalse();
    }

    [Test]
    public async Task Store_PolicyConfirmEnablesWithoutUserSetting()
    {
        var state = CodeExecutionGate.Evaluate(UpdateChannel.Store, new MachinePolicy { ExecuteCode = "confirm" }, null);
        await Assert.That(state.Enabled).IsTrue();
        await Assert.That(state.Source).IsEqualTo(CodeExecutionSource.Policy);
    }

    [Test]
    public async Task PolicyOffBeatsUserSetting_InEveryChannel()
    {
        var policy = new MachinePolicy { ExecuteCode = "off" };
        foreach (var channel in Enum.GetValues<UpdateChannel>())
        {
            await Assert.That(CodeExecutionGate.Evaluate(channel, policy, "confirm").Enabled).IsFalse();
            await Assert.That(CodeExecutionGate.Refusal("revit_execute_code", channel, policy, "confirm")).IsEqualTo(policy.CodeStepRefusal());
        }
    }

    [Test]
    public async Task UnreadablePolicy_KeepsCodeOff()
    {
        var policy = MachinePolicy.FailClosed("x");
        await Assert.That(CodeExecutionGate.Evaluate(UpdateChannel.Store, policy, "confirm").Enabled).IsFalse();
        await Assert.That(CodeExecutionGate.Refusal("execute-code", UpdateChannel.Store, policy, "confirm")).IsEqualTo(MachinePolicy.UnreadableRefusal);
        await Assert.That(CodeExecutionGate.NeedsUserSetting(UpdateChannel.Store, policy)).IsFalse();
    }

    [Test]
    public async Task OtherChannels_AreOnWithoutPolicy()
    {
        foreach (var channel in new[] { UpdateChannel.Release, UpdateChannel.Managed, UpdateChannel.Dev })
        {
            var state = CodeExecutionGate.Evaluate(channel, MachinePolicy.Empty, null);
            await Assert.That(state.Enabled).IsTrue();
            await Assert.That(state.Source).IsEqualTo(CodeExecutionSource.Default);
        }
    }

    [Test]
    public async Task Refusal_NamesHowToEnable_AndOnlyHitsCodeCommands()
    {
        var refusal = CodeExecutionGate.Refusal("revit_execute_code", UpdateChannel.Store, MachinePolicy.Empty, null);
        await Assert.That(refusal).IsEqualTo(CodeExecutionGate.StoreRefusal);
        await Assert.That(refusal!).Contains("administrator");
        await Assert.That(refusal).Contains("ExecuteCode policy");
        await Assert.That(CodeExecutionGate.Refusal("execute-code", UpdateChannel.Store, MachinePolicy.Empty, null)).IsEqualTo(refusal);
        await Assert.That(CodeExecutionGate.Refusal("revit_move", UpdateChannel.Store, MachinePolicy.Empty, null)).IsNull();
        await Assert.That(CodeExecutionGate.Refusal("ping", UpdateChannel.Store, MachinePolicy.Empty, null)).IsNull();
        await Assert.That(CodeExecutionGate.Refusal("revit_execute_code", UpdateChannel.Store, MachinePolicy.Empty, "confirm")).IsEqualTo(CodeExecutionGate.StoreRefusal);
    }

    [Test]
    public async Task Summary_ShowsEffectiveStateOnlyInStore()
    {
        var off = CodeExecutionGate.Summarize(UpdateChannel.Store, MachinePolicy.Empty, null);
        await Assert.That(off["executeCode"]).IsEqualTo("off");
        await Assert.That(off["executeCodeSource"]).IsEqualTo("store-default");
        await Assert.That(off["updateChannel"]).IsEqualTo("store");
        var user = CodeExecutionGate.Summarize(UpdateChannel.Store, MachinePolicy.Empty, "confirm");
        await Assert.That(user["executeCode"]).IsEqualTo("off");
        await Assert.That(user["executeCodeSource"]).IsEqualTo("store-default");
        var policy = CodeExecutionGate.Summarize(UpdateChannel.Store, new MachinePolicy { ExecuteCode = "off" }, "confirm");
        await Assert.That(policy["executeCode"]).IsEqualTo("off");
        await Assert.That(policy["executeCodeSource"]).IsEqualTo("policy");
        var release = CodeExecutionGate.Summarize(UpdateChannel.Release, MachinePolicy.Empty, null);
        await Assert.That(release.ContainsKey("executeCode")).IsFalse();
        await Assert.That(release.ContainsKey("updateChannel")).IsFalse();
    }

    [Test]
    public async Task IsCodeCommand_AcceptsToolAndCommandNames()
    {
        await Assert.That(MachinePolicy.IsCodeCommand("revit_execute_code")).IsTrue();
        await Assert.That(MachinePolicy.IsCodeCommand("execute-code")).IsTrue();
        await Assert.That(MachinePolicy.IsCodeCommand("process-models")).IsFalse();
        await Assert.That(MachinePolicy.IsCodeCommand(null)).IsFalse();
    }

    // The gate keys on the command alone: revit_execute_code with dry_run=true reaches the add-in as the same
    // "execute-code" command and is refused before any argument is read, so a dry run cannot compile or run code.
    [Test]
    [Arguments("revit_execute_code")]
    [Arguments("execute-code")]
    public async Task DryRunOfCode_IsRefusedWhenStoreDefaultIsOff(string command)
    {
        await Assert.That(CodeExecutionGate.Refusal(command, UpdateChannel.Store, MachinePolicy.Empty, null)).IsEqualTo(CodeExecutionGate.StoreRefusal);
        await Assert.That(CodeExecutionGate.Refusal(command, UpdateChannel.Store, MachinePolicy.Empty, "off")).IsEqualTo(CodeExecutionGate.StoreRefusal);
    }

    [Test]
    [Arguments("revit_execute_code")]
    [Arguments("execute-code")]
    public async Task DryRunOfCode_IsRefusedWhenPolicyIsOff(string command)
    {
        var policy = new MachinePolicy { ExecuteCode = "off" };
        foreach (var channel in Enum.GetValues<UpdateChannel>())
            await Assert.That(CodeExecutionGate.Refusal(command, channel, policy, "confirm")).IsEqualTo(policy.CodeStepRefusal());
        await Assert.That(policy.Refusal(command)).IsEqualTo(policy.CodeStepRefusal());
    }
}
