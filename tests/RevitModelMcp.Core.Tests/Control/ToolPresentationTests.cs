using RevitModelMcp.Core.Activity;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Policy;
using RevitModelMcp.Core.Updates;
using TUnit.Core.Enums;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ToolPresentationTests
{
    private const string Schema = """
        {"type":"object","properties":{"label":{"type":"string","description":"A label","maxLength":4},"count":{"type":"integer","minimum":1,"maximum":10},"size":{"type":"number"},"enabled":{"type":"boolean"},"choice":{"type":"string","enum":["one","two"]}},"required":["count"],"additionalProperties":false}
        """;

    [Test]
    public async Task Defaults_HaveReadableLabelsAndStableVariedIcons()
    {
        await Assert.That(ToolPresentationStore.Defaults("count_walls").Label).IsEqualTo("Count walls");
        await Assert.That(ToolPresentationStore.Defaults("count_walls").Icon).IsEqualTo(ToolPresentationStore.Defaults("count_walls").Icon);
        await Assert.That(ToolPresentationStore.Defaults("count_walls").TransactionMode).IsEqualTo("auto");
        var icons = Enumerable.Range(0, 40).Select(index => ToolPresentationStore.Defaults("tool_" + index).Icon).Distinct().ToArray();
        await Assert.That(icons.Length > 1).IsTrue();
        await Assert.That(icons.All(ToolPresentationStore.BuiltInIcons.Contains)).IsTrue();
    }

    [Test]
    public async Task Store_RoundTripsAllPresentationFieldsAndDeletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolPresentationStore(directory);
            var presentation = ToolPresentationStore.Defaults("my_tool") with
            {
                Label = "My renamed tool",
                Icon = "Help",
                Hidden = true,
                Order = 8,
                TransactionMode = "group",
                LastArguments = "{\"count\":2}",
                SmallPng = Convert.ToBase64String([1, 2, 3]),
                LargePng = Convert.ToBase64String([4, 5, 6])
            };
            store.Save("my_tool", presentation);
            await Assert.That(store.Get("my_tool")).IsEqualTo(presentation);
            await Assert.That(Directory.GetFiles(directory, "*.tmp").Length).IsEqualTo(0);
            store.Delete("my_tool");
            store.Delete("my_tool");
            await Assert.That(store.Get("my_tool")).IsEqualTo(ToolPresentationStore.Defaults("my_tool"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    [Arguments("not json")]
    [Arguments("null")]
    [Arguments("{}")]
    public async Task Store_MissingOrCorruptFilesUseDefaults(string corrupt)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolPresentationStore(directory);
            await Assert.That(store.Get("my_tool")).IsEqualTo(ToolPresentationStore.Defaults("my_tool"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "my_tool.json"), corrupt);
            await Assert.That(store.Get("my_tool")).IsEqualTo(ToolPresentationStore.Defaults("my_tool"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("bad\u200blabel")]
    [Arguments("bad\nlabel")]
    public async Task Labels_RejectInvalidText(string label)
    {
        await Assert.That(() => ToolPresentationStore.Validate(ToolPresentationStore.Defaults("my_tool") with { Label = label })).Throws<ArgumentException>();
    }

    [Test]
    public async Task Presentation_RejectsInvalidIconArgumentsAndOversizeLabels()
    {
        var defaults = ToolPresentationStore.Defaults("my_tool");
        await Assert.That(() => ToolPresentationStore.Validate(defaults with { Icon = "../foreign" })).Throws<ArgumentException>();
        await Assert.That(() => ToolPresentationStore.Validate(defaults with { Label = new string('x', 81) })).Throws<ArgumentException>();
        await Assert.That(() => ToolPresentationStore.Validate(defaults with { LastArguments = "[]" })).Throws<ArgumentException>();
    }


    [Test]
    public async Task Presentation_DoesNotChangeApprovedRevisionsOrCatalogValidation()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var tools = new ToolStore(Path.Combine(directory, "tools"));
            var presentation = new ToolPresentationStore(Path.Combine(directory, "tool-presentation"));
            var definition = new ToolDefinition
            {
                Name = "my_tool",
                Description = "A saved tool",
                Parameters = Schema,
                Code = ToolDefinition.Normalize("return 1;"),
                Revision = ToolDefinition.Hash("return 1;")
            };
            tools.Save(definition);
            var binding = tools.Get("my_tool").ApprovalBinding();
            presentation.Save("my_tool", ToolPresentationStore.Defaults("my_tool") with { Label = "Another label", Hidden = true });
            await Assert.That(tools.Get("my_tool").ApprovalBinding()).IsEqualTo(binding);
            await Assert.That(tools.Get("my_tool").Revision).IsEqualTo(definition.Revision);
            await Assert.That(tools.List(out var invalid).Count).IsEqualTo(1);
            await Assert.That(invalid.Count).IsEqualTo(0);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Slots_AreStableOrderedHideToolsAndLimitOverflow()
    {
        var names = Enumerable.Range(0, 30).Select(index => "tool_" + index.ToString("D2")).ToArray();
        ToolPresentation Presentation(string name) => ToolPresentationStore.Defaults(name) with
        {
            Order = name == "tool_29" ? -1 : 0,
            Hidden = name == "tool_00"
        };
        var assigned = ToolRibbonSlots.Assign(names.AsEnumerable().Reverse(), Presentation);
        await Assert.That(string.Join(",", assigned)).IsEqualTo(string.Join(",", ToolRibbonSlots.Assign(names, Presentation)));
        await Assert.That(assigned.Count).IsEqualTo(24);
        await Assert.That(assigned[0]).IsEqualTo("tool_29");
        await Assert.That(assigned.Contains("tool_00")).IsFalse();
        await Assert.That(assigned.Contains("tool_28")).IsFalse();
        await Assert.That(ToolRibbonSlots.Assign([], Presentation).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Form_ExposesSchemaAndUsesSharedValidator()
    {
        var form = new ToolParameterForm(Schema);
        await Assert.That(form.Fields.Count).IsEqualTo(5);
        var count = form.Fields.Single(field => field.Name == "count");
        await Assert.That(count.Required).IsTrue();
        await Assert.That(count.Type).IsEqualTo("integer");
        await Assert.That(count.Minimum).IsEqualTo("1");
        await Assert.That(count.Maximum).IsEqualTo("10");
        await Assert.That(form.Fields[0].Description).IsEqualTo("A label");
        await Assert.That(form.Fields[0].MaxLength).IsEqualTo(4);
        await Assert.That(form.Fields.Single(field => field.Name == "choice").Choices.Count).IsEqualTo(2);
        var json = form.Validate(new Dictionary<string, string?>
        {
            ["count"] = "2",
            ["label"] = "test",
            ["size"] = "1.5",
            ["enabled"] = "true",
            ["choice"] = "two"
        });
        var args = ToolArguments.Validate(Schema, json);
        await Assert.That(args["count"]).IsEqualTo(2L);
        await Assert.That(args["size"]).IsEqualTo(1.5);
        await Assert.That((bool)args["enabled"]).IsTrue();
        await Assert.That(new ToolParameterForm("{\"type\":\"object\"}").Fields.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("count", "0")]
    [Arguments("count", "11")]
    [Arguments("count", "1.5")]
    [Arguments("count", "9223372036854775808")]
    [Arguments("label", "12345")]
    [Arguments("choice", "three")]
    [Arguments("size", "NaN")]
    [Arguments("enabled", "yes")]
    [Arguments("foreign", "value")]
    public async Task Form_ReportsParameterNames(string name, string value)
    {
        var form = new ToolParameterForm(Schema);
        var values = new Dictionary<string, string?>
        {
            ["count"] = "2",
            [name] = value
        };
        var error = "";
        try { form.Validate(values); }
        catch (ArgumentException exception) { error = exception.Message; }
        await Assert.That(error.Contains("args." + name)).IsTrue();
    }

    [Test]
    public async Task Form_MissingRequiredAndOptionalArguments()
    {
        var form = new ToolParameterForm(Schema);
        await Assert.That(() => form.Validate(new Dictionary<string, string?>())).Throws<ArgumentException>();
        var json = form.Validate(new Dictionary<string, string?>
        {
            ["count"] = "1",
            ["size"] = null
        });
        await Assert.That(ToolArguments.Validate(Schema, json).Count).IsEqualTo(1);
    }

    [Test]
    public async Task RibbonAndMcpConfirmationsRemainIndependent()
    {
        var ribbon = new ToolRibbonSession();
        var mcp = new ToolSession();
        ribbon.Confirm("my_tool", "revision1");
        await Assert.That(ribbon.RequiresReview("my_tool", "revision1")).IsFalse();
        await Assert.That(mcp.RequiresConfirmation("my_tool", "revision1", "read", true)).IsTrue();
        mcp.Confirm("my_tool", "revision2");
        await Assert.That(ribbon.RequiresReview("my_tool", "revision2")).IsTrue();
        await Assert.That(ribbon.RequiresReview("other_tool", "revision1")).IsTrue();
    }

    [Test]
    public async Task Refusal_PreservesLockAndPolicyMessages()
    {
        var policy = new MachinePolicy { ExecuteCode = "off" };
        var refusal = CodeExecutionGate.Refusal("run-tool", UpdateChannel.Release, policy, null);
        await Assert.That(ToolRibbonSession.Refusal(false, "locked", refusal)).IsEqualTo(refusal);
        await Assert.That(ToolRibbonSession.Refusal(true, "locked", refusal)).IsEqualTo("locked");
        await Assert.That(ToolRibbonSession.Refusal(false, "locked", null)).IsNull();
        var storeRefusal = CodeExecutionGate.Refusal("run-tool", UpdateChannel.Store, MachinePolicy.Empty, null);
        await Assert.That(ToolRibbonSession.Refusal(false, "locked", storeRefusal)).IsEqualTo(CodeExecutionGate.StoreRefusal);
        var denied = new MachinePolicy { ToolDeny = ["run-tool"] }.Refusal("run-tool");
        await Assert.That(ToolRibbonSession.Refusal(false, "locked", denied)).IsEqualTo(denied);
    }

    [Test]
    public async Task SavedToolActivity_UsesIdentifierNotCommandOrLabel()
    {
        await Assert.That(ActivityTitleBuilder.Build(new ActivityEntry
        {
            Command = "run-tool",
            ToolName = "color_missing_parameter"
        })).IsEqualTo("Ran color_missing_parameter");
    }

    [Test]
    public async Task Delete_WithoutPresentationOrDirectorySucceeds()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new ToolPresentationStore(directory);
        await Assert.That(store.Delete("my_tool")).IsNull();
        await Assert.That(Directory.Exists(directory)).IsFalse();
    }

    [Test]
    [RunOn(OS.Windows)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Delete_LockedOrReadOnlyPresentationDoesNotFailToolDeletion(bool locked)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var presentationDirectory = Path.Combine(directory, "tool-presentation");
        var path = Path.Combine(presentationDirectory, "my_tool.json");
        try
        {
            var tools = new ToolStore(Path.Combine(directory, "tools"));
            var presentation = new ToolPresentationStore(presentationDirectory);
            tools.Save(new ToolDefinition
            {
                Name = "my_tool",
                Description = "A saved tool",
                Parameters = Schema,
                Code = ToolDefinition.Normalize("return 1;"),
                Revision = ToolDefinition.Hash("return 1;")
            });
            presentation.Save("my_tool", ToolPresentationStore.Defaults("my_tool"));
            if (!locked) File.SetAttributes(path, FileAttributes.ReadOnly);
            using (var handle = locked ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            {
                tools.Delete("my_tool");
                var note = presentation.Delete("my_tool");
                await Assert.That(tools.List().Count).IsEqualTo(0);
                await Assert.That(note).IsEqualTo("Some local presentation data could not be removed.");
                await Assert.That(note!.Contains(directory)).IsFalse();
            }
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task ExistingPresentation_WithoutTransactionModeDefaultsToAuto()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "my_tool.json"),
                """{"Label":"Legacy label","Icon":"Mytools","Hidden":false,"Order":0,"LastArguments":"{}","SmallPng":"","LargePng":""}""");
            var presentation = new ToolPresentationStore(directory).Get("my_tool");
            await Assert.That(presentation.Label).IsEqualTo("Legacy label");
            await Assert.That(presentation.TransactionMode).IsEqualTo("auto");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Move_PersistsOrderAndPreservesOtherPresentationFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolPresentationStore(directory);
            var names = new[] { "tool_a", "tool_b", "tool_c" };
            var customized = ToolPresentationStore.Defaults("tool_b") with
            {
                Label = "Hidden tool",
                Hidden = true,
                TransactionMode = "group"
            };
            store.Save("tool_b", customized);
            await Assert.That(store.Move(names, "tool_c", -1)).IsTrue();
            await Assert.That(store.Move(names, "tool_a", 1)).IsTrue();
            await Assert.That(store.Move(names, "tool_c", -1)).IsFalse();
            await Assert.That(store.Move(names, "tool_b", 1)).IsFalse();
            await Assert.That(string.Join(",", ToolRibbonSlots.Assign(names, store.Get))).IsEqualTo("tool_c,tool_a");
            var stored = store.Get("tool_b");
            await Assert.That(stored with { Order = 0 }).IsEqualTo(customized);
            store.Save("tool_b", stored with { Hidden = false });
            await Assert.That(string.Join(",", ToolRibbonSlots.Assign(names, store.Get))).IsEqualTo("tool_c,tool_a,tool_b");
            await Assert.That(() => store.Save("tool_a", ToolPresentationStore.Defaults("tool_a") with { TransactionMode = "none" })).Throws<ArgumentException>();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
