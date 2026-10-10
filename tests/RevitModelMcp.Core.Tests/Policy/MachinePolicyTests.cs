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
    [Arguments("ExecuteCode", "maybe")]
    [Arguments("LogRetentionDays", -4)]
    [Arguments("LogRetentionDays", 99999)]
    [Arguments("ReadOnly", 2)]
    [Arguments("ReadOnly", "yes")]
    [Arguments("UpdateMode", "Sometimes")]
    [Arguments("Unknown", 1)]
    [Arguments("ToolDeny", 5)]
    public async Task Parse_InvalidValueFailsClosed(string name, object value)
    {
        var policy = MachinePolicy.Parse(new Dictionary<string, object> { ["ReadOnly"] = 0, [name] = value });
        await AssertFailClosed(policy);
    }

    [Test]
    public async Task Parse_ReadOnlyObjectFailsClosed()
    {
        await AssertFailClosed(MachinePolicy.Parse(new Dictionary<string, object> { ["ReadOnly"] = new object() }));
    }

    [Test]
    public async Task Parse_DuplicateNamesDifferingInCaseFailClosed()
    {
        await AssertFailClosed(MachinePolicy.Parse(new Dictionary<string, object> { ["ReadOnly"] = 0, ["readonly"] = 1 }));
    }

    [Test]
    public async Task Parse_BlankTextMeansUnset()
    {
        var policy = MachinePolicy.Parse(new Dictionary<string, object> { ["ExecuteCode"] = " ", ["LogDir"] = "" });
        await Assert.That(policy.IsEmpty).IsTrue();
    }

    [Test]
    public async Task ParseJson_ReadsFile()
    {
        var policy = MachinePolicy.ParseJson("""{"ReadOnly":true,"ToolDeny":["execute-code"],"UpdateMode":"off","LogDir":"D:\\logs"}""");
        await Assert.That(policy.Unreadable).IsFalse();
        await Assert.That(policy.IsReadOnly).IsTrue();
        await Assert.That(policy.ToolDeny!.Single()).IsEqualTo("execute-code");
        await Assert.That(policy.UpdateMode).IsEqualTo("off");
        await Assert.That(policy.LogDir).IsEqualTo(@"D:\logs");
    }

    [Test]
    public async Task ParseJson_AcceptsCamelCaseKeysAndSingleStringLists()
    {
        var policy = MachinePolicy.ParseJson("""{"readOnly":true,"toolDeny":"revit_delete","toolAllow":["move"],"executeCode":"OFF","httpEnabled":false,"logRetentionDays":30}""");
        await Assert.That(policy.Unreadable).IsFalse();
        await Assert.That(policy.IsReadOnly).IsTrue();
        await Assert.That(policy.ToolDeny!.Single()).IsEqualTo("revit_delete");
        await Assert.That(policy.ToolAllow!.Single()).IsEqualTo("move");
        await Assert.That(policy.ExecuteCode).IsEqualTo("off");
        await Assert.That(policy.HttpEnabled).IsFalse();
        await Assert.That(policy.LogRetentionDays).IsEqualTo(30);
    }

    [Test]
    [Arguments("{nope")]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    [Arguments("[]")]
    [Arguments("null")]
    [Arguments("""{"ReadOnly":"yes"}""")]
    [Arguments("""{"ReadOnly":false,"ToolDeny":5}""")]
    [Arguments("""{"ReadOnly":false,"ToolDeny":[1,2]}""")]
    [Arguments("""{"ReadOnly":false,"ExecuteCode":"sometimes"}""")]
    [Arguments("""{"ReadOnly":false,"ReadOnlyy":true}""")]
    [Arguments("""{"ReadOnly":false,"ReadOnly":true}""")]
    [Arguments("""{"ReadOnly":null}""")]
    public async Task ParseJson_AnyProblemFailsClosed(string? json)
    {
        await AssertFailClosed(MachinePolicy.ParseJson(json));
    }

    [Test]
    public async Task FailClosed_RefusesCodeAndNamesTheProblem()
    {
        var policy = MachinePolicy.ParseJson("{nope");
        var expected = "Blocked by organization policy: the policy file could not be read.";
        await Assert.That(policy.ReadOnlyRefusal()).IsEqualTo(expected);
        await Assert.That(policy.Refusal("execute-code")).IsEqualTo(expected);
        await Assert.That(policy.CodeStepRefusal()).IsEqualTo(expected);
        await Assert.That(policy.Refusal("ping")).IsNull();
        await Assert.That(policy.Refusal("document-info")).IsNull();
        await Assert.That(policy.ToSummary()["policyUnreadable"]).IsEqualTo("true");
    }

    [Test]
    public async Task Merge_UnreadableSourceWinsOverEverything()
    {
        var good = new MachinePolicy { ReadOnly = false, ExecuteCode = "confirm", HttpEnabled = true };
        await AssertFailClosed(MachinePolicy.Merge(good, MachinePolicy.FailClosed("x")));
        await AssertFailClosed(MachinePolicy.Merge(MachinePolicy.FailClosed("x"), good));
    }

    [Test]
    public async Task EffectiveHttpEnabled_PolicyBeatsRequestedValue()
    {
        await Assert.That(new MachinePolicy { HttpEnabled = false }.EffectiveHttpEnabled(true)).IsFalse();
        await Assert.That(new MachinePolicy { HttpEnabled = true }.EffectiveHttpEnabled(false)).IsTrue();
        await Assert.That(MachinePolicy.Empty.EffectiveHttpEnabled(true)).IsTrue();
        await Assert.That(MachinePolicy.FailClosed("x").EffectiveHttpEnabled(true)).IsFalse();
    }

    private static async Task AssertFailClosed(MachinePolicy policy)
    {
        await Assert.That(policy.Unreadable).IsTrue();
        await Assert.That(policy.IsEmpty).IsFalse();
        await Assert.That(policy.IsReadOnly).IsTrue();
        await Assert.That(policy.CodeExecutionOff).IsTrue();
        await Assert.That(policy.UpdateMode).IsEqualTo("off");
        await Assert.That(policy.HttpEnabled).IsFalse();
        await Assert.That(policy.ReadOnlyRefusal()).IsEqualTo(MachinePolicy.UnreadableRefusal);
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
    public async Task Refusal_AliasesMapToolNamesToCommands()
    {
        await Assert.That(MachinePolicy.NormalizeToolName("revit_run_actions")).IsEqualTo("batch");
        await Assert.That(MachinePolicy.NormalizeToolName("revit_batch_start")).IsEqualTo("batch-supervisor-start");
        var denyTool = new MachinePolicy { ToolDeny = ["revit_batch_start", "revit_run_actions"] };
        await Assert.That(denyTool.Refusal("batch-supervisor-start")).StartsWith(MachinePolicy.RefusalPrefix);
        await Assert.That(denyTool.Refusal("batch")).StartsWith(MachinePolicy.RefusalPrefix);
        var denyCommand = new MachinePolicy { ToolDeny = ["batch-supervisor-start", "batch"] };
        await Assert.That(denyCommand.Refusal("revit_batch_start")).StartsWith(MachinePolicy.RefusalPrefix);
        await Assert.That(denyCommand.Refusal("revit_run_actions")).StartsWith(MachinePolicy.RefusalPrefix);
        var allow = new MachinePolicy { ToolAllow = ["revit_run_actions"] };
        await Assert.That(allow.Refusal("batch")).IsNull();
        await Assert.That(allow.Refusal("batch-supervisor-start")).StartsWith(MachinePolicy.RefusalPrefix);
    }

    private static readonly HashSet<string> ExemptTools = ["revit_ping", "revit_jobs"];

    [Test]
    public async Task ToolDeny_RefusesEveryManifestTool()
    {
        var tools = ManifestToolNames();
        await Assert.That(tools.Count).IsGreaterThan(70);
        foreach (var tool in tools)
        {
            var policy = new MachinePolicy { ToolDeny = [tool] };
            var sent = tool switch { "revit_run_actions" => "batch", "revit_batch_start" => "batch-supervisor-start", _ => tool.Substring("revit_".Length).Replace('_', '-') };
            if (ExemptTools.Contains(tool))
            {
                await Assert.That(policy.Refusal(tool)).IsNull();
                await Assert.That(policy.Refusal(sent)).IsNull();
                continue;
            }
            await Assert.That(policy.Refusal(tool)).IsNotNull().Because(tool);
            await Assert.That(policy.Refusal(sent)).IsNotNull().Because(sent);
            var allow = new MachinePolicy { ToolAllow = ["revit_ping"] };
            await Assert.That(allow.Refusal(tool)).IsNotNull().Because(tool);
        }
    }

    private static List<string> ManifestToolNames()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "tool-manifest.json"))) directory = directory.Parent;
        if (directory is null) throw new FileNotFoundException("docs/tool-manifest.json was not found above the test output.");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "docs", "tool-manifest.json")));
        return document.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToList();
    }

    [Test]
    public async Task Refusal_CoversTagElementsAndCreateSchedule()
    {
        var policy = new MachinePolicy { ToolDeny = ["revit_tag_elements", "create-schedule"] };
        await Assert.That(policy.Refusal("tag-elements")).StartsWith(MachinePolicy.RefusalPrefix);
        await Assert.That(policy.Refusal("create-schedule")).StartsWith(MachinePolicy.RefusalPrefix);
        var allow = new MachinePolicy { ToolAllow = ["tag-elements"] };
        await Assert.That(allow.Refusal("tag-elements")).IsNull();
        await Assert.That(allow.Refusal("create-schedule")).StartsWith(MachinePolicy.RefusalPrefix);
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
