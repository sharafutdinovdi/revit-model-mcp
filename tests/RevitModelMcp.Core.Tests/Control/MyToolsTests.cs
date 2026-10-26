using System.Text;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class MyToolsTests
{
    private const string Schema = """
        {"type":"object","properties":{"label":{"type":"string","maxLength":4,"enum":["test","ok"]},"count":{"type":"integer","minimum":1,"maximum":10},"size":{"type":"number","minimum":0},"enabled":{"type":"boolean"}},"required":["count"],"additionalProperties":false}
        """;

    private static ToolDefinition Definition(string name = "my_tool", string code = "return 1;") => new()
    {
        Name = name,
        Description = "Test tool",
        Parameters = Schema,
        Code = ToolDefinition.Normalize(code),
        Revision = ToolDefinition.Hash(code),
        Mode = "action",
        TestedRevitYears = [2022, 2027],
        Created = "2026-10-26T00:00:00Z",
        Author = "test"
    };

    [Test]
    public async Task Hash_NormalizesLineEndingsAndOuterWhitespace()
    {
        await Assert.That(ToolDefinition.Hash(" \r\nreturn 1;\r\n")).IsEqualTo(ToolDefinition.Hash("return 1;\n"));
        await Assert.That(ToolDefinition.Hash("return 2;")).IsNotEqualTo(ToolDefinition.Hash("return 1;"));
        await Assert.That(ToolDefinition.Hash("abc")).IsEqualTo(CodeSource.CodeHash("abc\n"));
    }

    [Test]
    public async Task Arguments_ReturnTypedValues()
    {
        var args = ToolArguments.Validate(Schema, """{"label":"ok","count":2,"size":1.5,"enabled":true}""");
        await Assert.That(args["label"]).IsEqualTo("ok");
        await Assert.That(args["count"]).IsEqualTo(2L);
        await Assert.That(args["size"]).IsEqualTo(1.5);
        await Assert.That((bool)args["enabled"]).IsTrue();
    }

    [Test]
    [Arguments("{}", "required")]
    [Arguments("{\"count\":0}", "minimum")]
    [Arguments("{\"count\":11}", "maximum")]
    [Arguments("{\"count\":1.5}", "integer")]
    [Arguments("{\"count\":true}", "integer")]
    [Arguments("{\"count\":1,\"label\":\"wrong\"}", "maxLength")]
    [Arguments("{\"count\":1,\"label\":\"no\"}", "enum")]
    [Arguments("{\"count\":1,\"other\":0}", "unknown")]
    [Arguments("{\"count\":1,\"enabled\":\"true\"}", "boolean")]
    [Arguments("{\"count\":1,\"size\":null}", "number")]
    [Arguments("{\"count\":1,\"count\":2}", "Duplicate")]
    public async Task Arguments_RejectInvalidValuesWithClearErrors(string json, string message)
    {
        var error = Capture(() => ToolArguments.Validate(Schema, json));
        await Assert.That(error).Contains(message);
    }

    [Test]
    [Arguments("{\"type\":\"array\"}")]
    [Arguments("{\"type\":\"object\",\"additionalProperties\":true}")]
    [Arguments("{\"type\":\"object\",\"required\":[\"missing\"]}")]
    [Arguments("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"object\"}}}")]
    [Arguments("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"pattern\":\".*\"}}}")]
    [Arguments("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"maxLength\":-1}}}")]
    [Arguments("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"number\",\"minimum\":2,\"maximum\":1}}}")]
    [Arguments("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\",\"enum\":[true]}}}")]
    public async Task Schema_RejectsUnsupportedOrInconsistentRules(string schema)
    {
        await Assert.That(Capture(() => ToolArguments.ValidateSchema(schema)).Length > 0).IsTrue();
    }

    [Test]
    [Arguments("aa")]
    [Arguments("My_tool")]
    [Arguments("../tool")]
    [Arguments("tool\n")]
    [Arguments("a12345678901234567890123456789012345678901234567890")]
    public async Task Definition_RejectsUnsafeNames(string name)
    {
        await Assert.That(Capture(() => Definition(name).Validate())).Contains("name must match");
    }

    [Test]
    public async Task Definition_EnforcesUtf8ByteLimitAndModes()
    {
        var valid = Definition(code: new string('a', ToolStore.MaxSourceBytes - 1));
        valid.Validate();
        await Assert.That(Encoding.UTF8.GetByteCount(valid.Code)).IsEqualTo(ToolStore.MaxSourceBytes);
        await Assert.That(Capture(() => (valid with { Code = valid.Code + "a" }).Validate())).Contains("256 KB");
        await Assert.That(Capture(() => (valid with { Code = new string('é', ToolStore.MaxSourceBytes / 2 + 1) }).Validate())).Contains("256 KB");
        await Assert.That(Capture(() => (valid with { Mode = "unknown" }).Validate())).Contains("mode");
        await Assert.That(Capture(() => (valid with { TestedRevitYears = [2021] }).Validate())).Contains("years");
    }

    [Test]
    public async Task Store_PersistsRevisionsAndActivePointerAndDeletes()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolStore(folder);
            await Assert.That(store.List().Count).IsEqualTo(0);
            var initial = Definition();
            store.Save(initial);
            var updated = Definition(code: "return 2;");
            store.Save(updated);
            var reopened = new ToolStore(folder);
            await Assert.That(ToolJson.Serialize(reopened.Get(initial.Name))).IsEqualTo(ToolJson.Serialize(updated));
            await Assert.That(File.Exists(Path.Combine(folder, initial.Name, initial.Revision + ".json"))).IsTrue();
            await Assert.That(File.ReadAllText(Path.Combine(folder, initial.Name, "active"))).IsEqualTo(updated.Revision);
            await Assert.That(store.List().Count).IsEqualTo(1);
            await Assert.That(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories).Length).IsEqualTo(0);
            await Assert.That(Capture(() => reopened.Delete(initial.Name, initial.ApprovalBinding()))).Contains("The tool changed");
            reopened.Delete(initial.Name, updated.ApprovalBinding());
            await Assert.That(store.List().Count).IsEqualTo(0);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Test]
    [Arguments("code")]
    [Arguments("mode")]
    [Arguments("parameters")]
    [Arguments("revision")]
    public async Task Store_RefusesTamperedDefinition(string field)
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolStore(folder);
            var definition = Definition();
            store.Save(definition);
            var changed = field switch
            {
                "code" => definition with { Code = "return 999;" },
                "mode" => definition with { Mode = "read" },
                "parameters" => definition with { Parameters = "{\"type\":\"object\"}" },
                _ => definition with { Revision = new string('0', 64) }
            };
            File.WriteAllText(Path.Combine(folder, definition.Name, definition.Revision + ".json"), ToolJson.Serialize(changed));
            await Assert.That(Capture(() => store.Get(definition.Name))).Contains("revision not approved");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task Store_Enforces200ToolsButAllowsRevisionUpdate()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolStore(folder);
            for (var index = 0; index < ToolStore.MaxTools; index++) store.Save(Definition("tool_" + index));
            await Assert.That(Capture(() => store.Save(Definition("tool_extra")))).Contains("200");
            store.Save(Definition("tool_0", "return 2;"));
            await Assert.That(store.List().Count).IsEqualTo(200);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task Approval_BindsNameRevisionSchemaModeAndCannotBeReused()
    {
        var tool = Definition();
        var tokens = new DocumentConfirmationTokens();
        foreach (var changed in new[] { tool with { Name = "other_tool" }, Definition(code: "return 2;"), tool with { Mode = "read" }, tool with { Parameters = "{\"type\":\"object\"}" } })
        {
            var token = tokens.Issue("save-tool", tool.Name, tool.ApprovalBinding(), "");
            await Assert.That(tokens.Consume(token, "save-tool", changed.Name, changed.ApprovalBinding(), "")).IsNotEqualTo(DocumentConfirmationResult.Valid);
            await Assert.That(tokens.Consume(token, "save-tool", tool.Name, tool.ApprovalBinding(), "")).IsEqualTo(DocumentConfirmationResult.Invalid);
        }
    }

    [Test]
    public async Task RunResult_SerializesReturnValueThroughCodePipeline()
    {
        var response = CommandResponse<ActionResultData>.Ok("run-tool", new ActionResultData { ReturnValue = new Dictionary<string, object> { ["count"] = 2 } }, 0);
        await Assert.That(CommandResponseJsonSerializer.Serialize(response)).Contains("\"returnValue\":{\"count\":2}");
    }

    [Test]
    [Arguments("save-tool", true)]
    [Arguments("run-tool", true)]
    [Arguments("delete-tool", true)]
    [Arguments("list-tools", false)]
    [Arguments("get-tool", false)]
    public async Task Commands_EnforceCodePolicyAndReadOnlyGate(string command, bool locked)
    {
        var policy = RevitModelMcp.Core.Policy.MachinePolicy.ParseJson("{\"ExecuteCode\":\"off\"}");
        await Assert.That(RevitModelMcp.Core.Policy.MachinePolicy.IsCodeCommand(command)).IsTrue();
        await Assert.That(policy.Refusal(command)).Contains("running C# code is disabled");
        await Assert.That(ReadOnlyGatePolicy.RefusesSubmission(command)).IsEqualTo(locked);
        var parsed = ControlJobParser.Parse("{\"command\":\"" + command + "\",\"name\":\"my_tool\",\"toolArgs\":\"{}\"}");
        await Assert.That(parsed.Error).IsNull();
        await Assert.That(parsed.Action!.Name).IsEqualTo("my_tool");
    }

    [Test]
    public async Task Arguments_AcceptJsonIntegerNotationAndCountUnicodeCharacters()
    {
        var args = ToolArguments.Validate(Schema, "{\"count\":2e0}");
        await Assert.That(args["count"]).IsEqualTo(2L);
        var unicode = ToolArguments.Validate("{\"type\":\"object\",\"properties\":{\"label\":{\"type\":\"string\",\"maxLength\":1}}}", "{\"label\":\"😀\"}");
        await Assert.That(unicode["label"]).IsEqualTo("😀");
        await Assert.That(Capture(() => ToolArguments.Validate(Schema, "{\"count\":9223372036854775808}"))).Contains("Int64");
    }

    [Test]
    public async Task Store_RefusesImportedRevisionsAndMissingOrMalformedApprovalFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var imported = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var definition = Definition();
            var store = new ToolStore(folder);
            store.Save(definition);
            var destination = Path.Combine(imported, definition.Name);
            Directory.CreateDirectory(destination);
            foreach (var path in Directory.GetFiles(Path.Combine(folder, definition.Name)))
                File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
            await Assert.That(Capture(() => new ToolStore(imported).Get(definition.Name))).Contains("revision not approved");
            var pointer = Path.Combine(folder, definition.Name, "active");
            File.WriteAllText(pointer, "../untrusted");
            await Assert.That(Capture(() => store.Get(definition.Name))).Contains("revision not approved");
            File.WriteAllText(pointer, definition.Revision);
            var revision = Path.Combine(folder, definition.Name, definition.Revision + ".json");
            File.WriteAllText(revision, "{bad json");
            await Assert.That(Capture(() => store.Get(definition.Name))).Contains("revision not approved");
            store.Save(definition);
            File.Delete(Path.Combine(folder, definition.Name, definition.Revision + ".approved"));
            await Assert.That(Capture(() => store.Get(definition.Name))).Contains("revision not approved");
            store.Save(definition);
            File.WriteAllText(Path.Combine(folder, ".approval-key"), "invalid");
            await Assert.That(Capture(() => store.Get(definition.Name))).Contains("revision not approved");
        }
        finally
        {
            Directory.Delete(folder, true);
            Directory.Delete(imported, true);
        }
    }

    [Test]
    public async Task Arguments_PreserveLargeIntegerConstraintsAndEnums()
    {
        const string schema = """
            {"type":"object","properties":{"value":{"type":"integer","enum":[9007199254740992],"maximum":9007199254740992}}}
            """;
        await Assert.That(Capture(() => ToolArguments.Validate(schema, "{\"value\":9007199254740993}"))).Contains("maximum");
        var enumOnly = schema.Replace(",\"maximum\":9007199254740992", "");
        await Assert.That(Capture(() => ToolArguments.Validate(enumOnly, "{\"value\":9007199254740993}"))).Contains("enum");
    }

    private static string Capture(Action action)
    {
        try { action(); return ""; }
        catch (Exception exception) { return exception.Message; }
    }
}
