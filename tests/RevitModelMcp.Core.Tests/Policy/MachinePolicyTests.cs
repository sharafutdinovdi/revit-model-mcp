using RevitModelMcp.Core.Policy;

namespace RevitModelMcp.Core.Tests.Policy;

public sealed class MachinePolicyTests
{
    [Test]
    public async Task Parse_ReadsRegistryTypes()
    {
        var policy = MachinePolicy.Parse(new Dictionary<string, object>
        {
            ["ReadOnly"] = 1,
            ["ToolDeny"] = new[] { "revit_delete", " " },
            ["ExecuteCode"] = "OFF",
            ["UpdateMode"] = "notify",
            ["TrustedNetworkRoots"] = new[] { @"\\srv\share" },
            ["LogRetentionDays"] = 30,
            ["RedactPaths"] = 1,
            ["HttpEnabled"] = 0
        });
        await Assert.That(policy.IsReadOnly).IsTrue();
        await Assert.That(policy.ToolDeny!.Single()).IsEqualTo("revit_delete");
        await Assert.That(policy.ExecuteCode).IsEqualTo("off");
        await Assert.That(policy.UpdateMode).IsEqualTo("notify");
        await Assert.That(policy.TrustedNetworkRoots!.Single()).IsEqualTo(@"\\srv\share");
        await Assert.That(policy.LogRetentionDays).IsEqualTo(30);
        await Assert.That(policy.RedactPaths).IsTrue();
        await Assert.That(policy.HttpEnabled).IsFalse();
    }

    [Test]
    public async Task Parse_IgnoresInvalidValues()
    {
        var policy = MachinePolicy.Parse(new Dictionary<string, object>
        {
            ["ExecuteCode"] = "maybe",
            ["UpdateMode"] = "sometimes",
            ["LogRetentionDays"] = -4,
            ["ReadOnly"] = new object(),
            ["Unknown"] = 1
        });
        await Assert.That(policy.IsEmpty).IsTrue();
    }

    [Test]
    public async Task ParseJson_ReadsFile()
    {
        var policy = MachinePolicy.ParseJson("""{"ReadOnly":true,"ToolDeny":["execute-code"],"UpdateMode":"off","LogDir":"D:\\logs"}""");
        await Assert.That(policy.IsReadOnly).IsTrue();
        await Assert.That(policy.ToolDeny!.Single()).IsEqualTo("execute-code");
        await Assert.That(policy.UpdateMode).IsEqualTo("off");
        await Assert.That(policy.LogDir).IsEqualTo(@"D:\logs");
    }

    [Test]
    public async Task ParseJson_InvalidIsEmpty()
    {
        await Assert.That(MachinePolicy.ParseJson("{nope").IsEmpty).IsTrue();
        await Assert.That(MachinePolicy.ParseJson(null).IsEmpty).IsTrue();
        await Assert.That(MachinePolicy.ParseJson("""{"ReadOnly":"yes"}""").IsEmpty).IsTrue();
    }

    [Test]
    public async Task Merge_RegistryWinsPerValue()
    {
        var registry = new MachinePolicy { ReadOnly = false, UpdateMode = "off" };
        var file = new MachinePolicy { ReadOnly = true, UpdateMode = "auto", ExecuteCode = "off" };
        var merged = MachinePolicy.Merge(registry, file);
        await Assert.That(merged.IsReadOnly).IsFalse();
        await Assert.That(merged.UpdateMode).IsEqualTo("off");
        await Assert.That(merged.ExecuteCode).IsEqualTo("off");
    }

    [Test]
    public async Task Refusal_DenyAllowAndCode()
    {
        var policy = new MachinePolicy { ToolDeny = ["revit_delete"], ToolAllow = ["delete", "move", "execute-code"], ExecuteCode = "off" };
        await Assert.That(policy.Refusal("delete")).StartsWith(MachinePolicy.RefusalPrefix);
        await Assert.That(policy.Refusal("move")).IsNull();
        await Assert.That(policy.Refusal("copy")).StartsWith(MachinePolicy.RefusalPrefix);
        await Assert.That(policy.Refusal("execute-code")).StartsWith(MachinePolicy.RefusalPrefix);
        await Assert.That(new MachinePolicy { ToolAllow = ["move"] }.Refusal("ping")).IsNull();
        await Assert.That(new MachinePolicy { ToolDeny = ["jobs"] }.Refusal("jobs")).IsNull();
        await Assert.That(MachinePolicy.Empty.Refusal("delete")).IsNull();
        await Assert.That(MachinePolicy.Empty.Refusal(null)).IsNull();
    }

    [Test]
    public async Task ReadOnlyRefusal_OnlyWhenEnforced()
    {
        await Assert.That(new MachinePolicy { ReadOnly = true }.ReadOnlyRefusal()).StartsWith(MachinePolicy.RefusalPrefix);
        await Assert.That(new MachinePolicy { ReadOnly = false }.ReadOnlyRefusal()).IsNull();
    }

    [Test]
    public async Task ToSummary_HidesPaths()
    {
        var summary = new MachinePolicy { LogDir = @"C:\secret", TrustedNetworkRoots = [@"\\a\b"], ToolDeny = ["revit_delete"] }.ToSummary();
        await Assert.That(summary.ContainsKey("logDir")).IsFalse();
        await Assert.That(summary["trustedNetworkRootCount"]).IsEqualTo("1");
        await Assert.That(summary["toolDeny"]).IsEqualTo("delete");
    }
}
