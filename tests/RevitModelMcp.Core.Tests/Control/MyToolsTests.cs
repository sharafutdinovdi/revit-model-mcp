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
    [Arguments(true)]
    [Arguments(false)]
    public async Task References_UseBoundIdentityAndExcludeOtherExtensionOwners(bool merged)
    {
        var owner = new CodeSource.AssemblyReference(
            merged ? "RevitModelMcp, Version=1.0.0.0" : "Nice3point.Revit.Extensions, Version=2027.0.0.0",
            "/ours/owner.dll", true);
        var foreign = new CodeSource.AssemblyReference("Nice3point.Revit.Extensions, Version=2026.0.0.0", "/foreign/extensions.dll", true);
        var otherMerged = new CodeSource.AssemblyReference("OtherAddin, Version=1.0.0.0", "/foreign/merged.dll", true);
        var duplicate = owner with { Location = "/foreign/same-identity.dll" };
        var framework = new CodeSource.AssemblyReference("System.Runtime, Version=8.0.0.0", "/framework/runtime.dll");
        var references = CodeSource.SelectReferences([foreign, otherMerged, duplicate, framework, owner,
            framework with { Location = "/duplicate/runtime.dll" }], [owner]);
        await Assert.That(references.Count).IsEqualTo(2);
        await Assert.That(references[0]).IsEqualTo(owner);
        await Assert.That(references[1]).IsEqualTo(framework);
    }

    [Test]
    public async Task References_DeduplicateSimpleNamesAcrossVersionsAndFileNames()
    {
        var owner = new CodeSource.AssemblyReference("RevitModelMcp, Version=1.0.0.0", "/ours/addin.dll", true);
        var bound = new CodeSource.AssemblyReference("RevitAPI, Version=27.0.0.0", "/ours/api.dll");
        var foreign = new CodeSource.AssemblyReference("RevitAPI, Version=26.0.0.0", "/foreign/RevitAPI.dll");
        var references = CodeSource.SelectReferences([foreign, bound, owner], [owner, bound]);
        await Assert.That(references.Count).IsEqualTo(2);
        await Assert.That(references[1]).IsEqualTo(bound);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReadOnlyLock_RefusesSavedReadToolsAndExecuteCodeRegardlessOfDryRun(bool dryRun)
    {
        var readTool = ActionJobParser.Parse("run-tool", new ControlJobContract
        {
            Name = "read_tool",
            ToolMode = "read",
            DryRun = dryRun
        });
        var code = ActionJobParser.Parse("execute-code", new ControlJobContract
        {
            Code = "return 1;",
            DryRun = dryRun
        });
        await Assert.That(readTool.Error).IsNull();
        await Assert.That(code.Error).IsNull();
        await Assert.That(ReadOnlyGatePolicy.RefusesSubmission(readTool.Command)).IsTrue();
        await Assert.That(ReadOnlyGatePolicy.RefusesSubmission(code.Command)).IsTrue();
    }

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


    [Test]
    [Arguments("read", false)]
    [Arguments("read", true)]
    [Arguments("action", true)]
    [Arguments("action", false)]
    public async Task Session_RequiresFirstRunConfirmationForEveryRevision(string mode, bool dryRun)
    {
        var session = new ToolSession();
        var tool = Definition() with { Mode = mode };
        await Assert.That(session.RequiresConfirmation(tool.Name, tool.Revision, mode, dryRun)).IsTrue();
        session.Confirm(tool.Name, tool.Revision);
        await Assert.That(session.RequiresConfirmation(tool.Name, tool.Revision, mode, dryRun)).IsEqualTo(mode == "action" && !dryRun);
        await Assert.That(session.RequiresConfirmation(tool.Name, ToolDefinition.Hash("return 2;"), mode, dryRun)).IsTrue();
        await Assert.That(session.RequiresConfirmation("other_tool", tool.Revision, mode, dryRun)).IsTrue();
        await Assert.That(new ToolSession().RequiresConfirmation(tool.Name, tool.Revision, mode, dryRun)).IsTrue();
    }

    [Test]
    public async Task Session_StoreSignatureDoesNotAuthorizeForgedRevision()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var session = new ToolSession();
            var store = new ToolStore(folder);
            var initial = Definition() with { Mode = "read" };
            store.Save(initial);
            session.Confirm(initial.Name, initial.Revision);
            var forged = Definition(code: "return 999;") with { Mode = "read" };
            store.Save(forged);
            var active = store.Get(forged.Name);
            await Assert.That(session.RequiresConfirmation(active.Name, active.Revision, active.Mode, false)).IsTrue();
            await Assert.That(typeof(ToolStore).IsPublic).IsFalse();
            await Assert.That(typeof(ToolDefinition).IsPublic).IsFalse();
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task ConfirmationText_ShowsValidatedArgsAndReplacement()
    {
        var previous = Definition() with { Mode = "read" };
        var tool = Definition(code: "return 2;") with { ReplacedRevision = previous.Revision };
        var args = ToolArguments.Validate(Schema, "{\"count\":2e0}");
        var text = ToolSession.RunText(tool, args);
        foreach (var expected in new[] { tool.Name, "mode action", tool.Revision[..12], tool.Created, "replaced " + previous.Revision[..12], "\"count\":2" })
            await Assert.That(text).Contains(expected);
        var save = ToolSession.SaveText(tool, previous);
        await Assert.That(save).Contains($"replaces {previous.Revision[..12]}, mode read -> action");
        var longText = ToolSession.RunText(tool, new Dictionary<string, object> { ["text"] = new string('x', 400) });
        var preview = longText.Split(["Validated args: "], StringSplitOptions.None)[1].Split([". C#"], StringSplitOptions.None)[0];
        await Assert.That(preview.Length).IsEqualTo(300);
        await Assert.That(ToolSession.CacheKey(tool.Revision)).IsEqualTo("tool:" + tool.Revision);
        var audit = ToolSession.AuditText(tool.Name, tool.Revision, args.Keys);
        await Assert.That(audit).Contains(tool.Name);
        await Assert.That(audit).Contains(tool.Revision);
        await Assert.That(audit).Contains("arg names=[\"count\"]");
        await Assert.That(audit).IsEqualTo($"Tool run: name={tool.Name}; revision={tool.Revision}; arg names=[\"count\"]");
    }

    [Test]
    [Arguments("\u200b")]
    [Arguments("\u202e")]
    [Arguments("\u2066")]
    [Arguments("\ufeff")]
    [Arguments("\U000e0001")]
    public async Task Validate_RejectsFormatCharacters(string character)
    {
        var tool = Definition();
        foreach (var changed in new[] { tool with { Name = "my" + character + "tool" }, tool with { Code = "//" + character }, tool with { Description = "test" + character }, tool with { Parameters = "{\"type\":\"object\",\"properties\":{\"a" + character + "\":{\"type\":\"string\"}}}" } })
            await Assert.That(Capture(changed.Validate)).Contains("Format characters");
    }

    [Test]
    public async Task Store_ListsValidAndInvalidAndDeletesBrokenFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolStore(folder);
            store.Save(Definition());
            Directory.CreateDirectory(Path.Combine(folder, "broken_tool"));
            File.WriteAllText(Path.Combine(folder, "broken_tool", "active"), "bad");
            var tools = store.List(out var invalid);
            await Assert.That(tools.Count).IsEqualTo(1);
            await Assert.That(invalid.Single().Name).IsEqualTo("broken_tool");
            await Assert.That(invalid.Single().Reason).IsEqualTo("revision not approved");
            await Assert.That(Capture(() => store.Get("broken_tool"))).Contains("revision not approved");
            var response = CommandResponse<ActionResultData>.Ok("list-tools", new ActionResultData { Tools = tools.ToList(), Invalid = invalid }, 0);
            await Assert.That(CommandResponseJsonSerializer.Serialize(response)).Contains("\"invalid\":[{\"name\":\"broken_tool\"");
            store.Delete("broken_tool", store.DeletionBinding("broken_tool"));
            store.List(out invalid);
            await Assert.That(invalid.Count).IsEqualTo(0);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task Store_CapsRevisionsAtTwentyAndKeepsActive()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ToolStore(folder);
            for (var index = 0; index < 25; index++) store.Save(Definition(code: $"return {index};"));
            var active = store.Get("my_tool");
            await Assert.That(active.Code).IsEqualTo("return 24;\n");
            await Assert.That(Directory.GetFiles(Path.Combine(folder, "my_tool"), "*.json").Length).IsEqualTo(20);
            await Assert.That(Directory.GetFiles(Path.Combine(folder, "my_tool"), "*.approved").Length).IsEqualTo(20);
            await Assert.That(File.Exists(Path.Combine(folder, "my_tool", Definition().Revision + ".json"))).IsFalse();
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task Store_LockBusyHasNoPathAndRetries()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ToolStore(folder);
            using (var held = new FileStream(Path.Combine(folder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.That(Capture(() => store.Save(Definition()))).IsEqualTo("My tools store is busy");
                await Assert.That(Capture(() => store.Delete("my_tool"))).IsEqualTo("My tools store is busy");
            }
            var retryLock = new FileStream(Path.Combine(folder, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var release = Task.Run(async () => { await Task.Delay(100); retryLock.Dispose(); });
            store.Save(Definition());
            await release;
            await Assert.That(store.Get("my_tool").Name).IsEqualTo("my_tool");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    [Arguments("get-tool")]
    [Arguments("save-tool")]
    public async Task Redaction_PreservesToolPreviewAndRedactsModelPath(string command)
    {
        const string source = "return @\"\\\\server\\share\\file.txt\";";
        var tool = Definition(code: source) with { Description = @"Review \\server\share\file.txt", Parameters = "{\"type\":\"object\",\"properties\":{\"label\":{\"type\":\"string\",\"description\":\"C:\\\\Private\\\\file.txt\"}}}" };
        var response = CommandResponse<ActionResultData>.Ok(command, new ActionResultData { Tool = tool, Summary = @"Model C:\Private\Model.rvt" }, 0);
        var json = CommandResponseJsonSerializer.RedactPaths(CommandResponseJsonSerializer.Serialize(response));
        var parsed = ToolJson.Deserialize<CommandResponse<ActionResultData>>(json);
        await Assert.That(parsed.Data!.Tool!.Code).IsEqualTo(tool.Code);
        await Assert.That(parsed.Data.Tool.Description).IsEqualTo(tool.Description);
        await Assert.That(parsed.Data.Tool.Parameters).IsEqualTo(tool.Parameters);
        await Assert.That(parsed.Data.Summary).IsEqualTo("Model Model.rvt");
    }

    private static string Capture(Action action)
    {
        try { action(); return ""; }
        catch (Exception exception) { return exception.Message; }
    }
}
