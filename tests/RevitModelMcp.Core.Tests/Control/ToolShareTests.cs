using System.Text;
using System.Text.Json.Nodes;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ToolShareTests
{
    private static ToolDefinition Definition() => new()
    {
        Name = "share_tool",
        Description = "Résumé",
        Parameters = "{\"type\":\"object\"}",
        Code = "var count = 1;\nreturn count;\n",
        Revision = ToolDefinition.Hash("var count = 1;\nreturn count;"),
        Mode = "read",
        TestedRevitYears = [2022, 2026]
    };

    private static ToolShare Share() => ToolShare.From(Definition(), new ToolPresentation { Label = "Café" });
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Refusal(byte[] bytes)
    {
        try { ToolShare.Parse(bytes); return ""; }
        catch (ArgumentException exception) { return exception.Message; }
    }

    [Test]
    public async Task CrossStoreRoundTripUsesTheImportersKey()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var first = new ToolStore(Path.Combine(folder, "first"));
            var second = new ToolStore(Path.Combine(folder, "second"));
            first.Save(Definition());
            var share = ToolShare.From(first.Get("share_tool"), new ToolPresentation { Label = "Café" });
            var parsed = ToolShare.Parse(Bytes(share.ToFileText()));
            second.Save(parsed.ToDefinition(parsed.Name, "import", "now", ""));
            var actual = second.Get("share_tool");
            await Assert.That(actual.Code).IsEqualTo(Definition().Code);
            await Assert.That(actual.Parameters).IsEqualTo(Definition().Parameters);
            await Assert.That(actual.Mode).IsEqualTo(Definition().Mode);
            await Assert.That(actual.Description).IsEqualTo(Definition().Description);
            await Assert.That(string.Join(",", actual.TestedRevitYears)).IsEqualTo("2022,2026");
            await Assert.That(File.ReadAllText(Path.Combine(folder, "first", ".approval-key"))).IsNotEqualTo(File.ReadAllText(Path.Combine(folder, "second", ".approval-key")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task LocalMetadataIsNotExportedOrFingerprinted()
    {
        var definition = Definition() with { Author = "author_marker", Created = "created_marker", ReplacedRevision = "replacement_marker" };
        var presentation = new ToolPresentation { Label = "Café", Hidden = true, Order = 987654, LastArguments = "{\"secret\":\"arguments_marker\"}" };
        var share = ToolShare.From(definition, presentation);
        foreach (var forbidden in new[] { "author_marker", "created_marker", "replacement_marker", "987654", "arguments_marker", "LastArguments", "lastArguments", "Hidden", "hidden", "Order", "order", "Author", "author", "Created", "created", "Revision", "revision", "ReplacedRevision", "replacedRevision", ".approved", "approval-key" })
            await Assert.That(share.ToFileText().Contains(forbidden, StringComparison.Ordinal)).IsFalse();
        await Assert.That(share.Fingerprint()).IsEqualTo(Share().Fingerprint());
    }

    [Test]
    [Arguments("name")]
    [Arguments("label")]
    [Arguments("description")]
    [Arguments("mode")]
    [Arguments("testedRevitYears")]
    [Arguments("transaction")]
    [Arguments("parameters")]
    [Arguments("code")]
    [Arguments("icon16")]
    [Arguments("icon32")]
    public async Task EveryExportedContentMemberChangesTheFingerprint(string member)
    {
        var share = Share();
        var changed = member switch
        {
            "name" => share with { Name = "other_tool" },
            "label" => share with { Label = "Other" },
            "description" => share with { Description = "Other" },
            "mode" => share with { Mode = "action" },
            "testedRevitYears" => share with { TestedRevitYears = [2026] },
            "transaction" => share with { Transaction = "group" },
            "parameters" => share with { Parameters = "{\"type\":\"object\",\"properties\":{}}" },
            "code" => share with { Code = "return 2;\n" },
            "icon16" => share with { Icon16 = "changed" },
            _ => share with { Icon32 = "changed" }
        };
        await Assert.That(changed.Fingerprint()).IsNotEqualTo(share.Fingerprint());
    }

    [Test]
    public async Task FormatVectorPinsTextAndFingerprint()
    {
        const string fingerprint = "a4b8da2db098ed6d65fa96e0c6f610503f841e71fd0279d62061cef5e74e13e5";
        var expected = """
            {
              "format": "revit-model-mcp/tool",
              "formatVersion": 1,
              "name": "share_tool",
              "label": "Café",
              "description": "Résumé",
              "mode": "read",
              "testedRevitYears": [2022,2026],
              "transaction": "auto",
              "parameters": "{\"type\":\"object\"}",
              "code": "var count = 1;\nreturn count;\n",
              "icon16": "",
              "icon32": "",
              "fingerprint": "a4b8da2db098ed6d65fa96e0c6f610503f841e71fd0279d62061cef5e74e13e5"
            }
            """ + "\n";
        await Assert.That(Share().Fingerprint()).IsEqualTo(fingerprint);
        await Assert.That(Share().ToFileText()).IsEqualTo(expected);
        await Assert.That(ToolShare.Parse(Bytes(expected)).Fingerprint()).IsEqualTo(fingerprint);
    }

    [Test]
    public async Task FileWhitespaceBomAndEscapingDoNotChangeContent()
    {
        var text = " \n" + Share().ToFileText().Replace("\n", "\r\n").Replace("Café", "Caf\\u00e9") + " \r\n";
        var parsed = ToolShare.Parse([0xEF, 0xBB, 0xBF, .. Bytes(text)]);
        await Assert.That(parsed.Fingerprint()).IsEqualTo(Share().Fingerprint());
    }

    [Test]
    [Arguments("code")]
    [Arguments("format")]
    [Arguments("newer")]
    [Arguments("version")]
    [Arguments("unknown")]
    [Arguments("duplicate")]
    [Arguments("missing")]
    [Arguments("type")]
    [Arguments("nested")]
    [Arguments("oversize")]
    [Arguments("utf8")]
    [Arguments("name")]
    [Arguments("schema")]
    [Arguments("unicode")]
    [Arguments("mode")]
    [Arguments("transaction")]
    [Arguments("label")]
    [Arguments("one_icon")]
    [Arguments("not_png")]
    [Arguments("dimensions")]
    [Arguments("unsorted")]
    [Arguments("years_duplicate")]
    [Arguments("fractional_year")]
    [Arguments("normalized")]
    [Arguments("array")]
    [Arguments("bad_base64")]
    [Arguments("icon_size")]
    public async Task ParseRefusesMalformedOrChangedFiles(string scenario)
    {
        var document = JsonNode.Parse(Share().ToFileText())!.AsObject();
        switch (scenario)
        {
            case "code": document["code"] = "return 2;\n"; break;
            case "format": document["format"] = "other"; break;
            case "newer": document["formatVersion"] = 2; break;
            case "version": document["formatVersion"] = 0; break;
            case "unknown": document["secret_member"] = "content_must_not_be_echoed"; break;
            case "missing": document.Remove("name"); break;
            case "type": document["name"] = 42; break;
            case "nested": document["code"] = new JsonObject { ["source"] = "content_must_not_be_echoed" }; break;
            case "name": document["name"] = "INVALID"; break;
            case "schema": document["parameters"] = "{\"type\":\"array\",\"secret_keyword\":true}"; break;
            case "unicode": document["description"] = "hidden\u200btext"; break;
            case "mode": document["mode"] = "execute"; break;
            case "transaction": document["transaction"] = "none"; break;
            case "label": document["label"] = "bad\tlabel"; break;
            case "one_icon": document["icon16"] = Png(16); break;
            case "not_png": document["icon16"] = Convert.ToBase64String(Bytes("not a png")); document["icon32"] = Convert.ToBase64String(Bytes("not a png")); break;
            case "dimensions": document["icon16"] = Png(32); document["icon32"] = Png(32); break;
            case "unsorted": document["testedRevitYears"] = new JsonArray(2026, 2022); break;
            case "years_duplicate": document["testedRevitYears"] = new JsonArray(2026, 2026); break;
            case "fractional_year": document["testedRevitYears"] = new JsonArray(2026.5); break;
            case "normalized": document["code"] = " return 1;\r\n"; break;
            case "bad_base64": document["icon16"] = "!"; document["icon32"] = "!"; break;
            case "icon_size": document["icon16"] = new string('A', 400004); document["icon32"] = new string('A', 400004); break;
        }
        var bytes = scenario switch
        {
            "duplicate" => Bytes(Share().ToFileText().Replace("{\n", "{\n  \"name\": \"duplicate\",\n")),
            "oversize" => new byte[ToolShare.MaxFileBytes + 1],
            "utf8" => [0xFF],
            "array" => Bytes("[]"),
            _ => Bytes(document.ToJsonString())
        };
        var refusal = Refusal(bytes);
        await Assert.That(refusal.Length > 0).IsTrue();
        await Assert.That(refusal.Contains("content_must_not_be_echoed", StringComparison.Ordinal)).IsFalse();
        await Assert.That(refusal.Contains("secret_keyword", StringComparison.Ordinal)).IsFalse();
        if (scenario == "newer") await Assert.That(refusal).Contains("newer Model MCP version");
        if (scenario == "code") await Assert.That(refusal).Contains("does not match its fingerprint");
    }

    private static string Png(int size)
    {
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, (byte)size, 0, 0, 0, (byte)size, 8, 6, 0, 0, 0, 0, 0, 0, 0];
        return Convert.ToBase64String(bytes);
    }

    [Test]
    public async Task PresentationPreservesLocalPreferences()
    {
        var share = Share() with { Icon16 = Png(16), Icon32 = Png(32), Transaction = "group" };
        var existing = new ToolPresentation { Label = "Before", Hidden = true, Order = 7, LastArguments = "{\"count\":2}" };
        var actual = share.ApplyTo(existing);
        await Assert.That(actual).IsEqualTo(existing with { Label = share.Label, SmallPng = share.Icon16, LargePng = share.Icon32, TransactionMode = "group" });
        await Assert.That(ToolShare.Parse(Bytes(share.ToFileText())).Icon16).IsEqualTo(share.Icon16);
    }

    [Test]
    [Arguments("label")]
    [Arguments("description")]
    [Arguments("parameters")]
    [Arguments("code")]
    public async Task PrivateDataFindsEveryBuiltInKindInEveryField(string field)
    {
        const string value = "C:\\Projects\\A.rvt D:\\\\Models\\\\B.rvt \\\\server\\share\\x \\\\\\\\srv01\\\\bim RSN://host/model https://example.com http://example.org user@example.com";
        var share = field switch
        {
            "label" => Share() with { Label = value },
            "description" => Share() with { Description = value },
            "parameters" => Share() with { Parameters = value },
            _ => Share() with { Code = value }
        };
        var result = share.FindPrivateData();
        await Assert.That(result.Findings.Count(finding => finding.Kind == "file path")).IsEqualTo(5);
        await Assert.That(result.Findings.Count(finding => finding.Kind == "web address")).IsEqualTo(2);
        await Assert.That(result.Findings.Count(finding => finding.Kind == "email address")).IsEqualTo(1);
        await Assert.That(result.Findings.All(finding => finding.Field == field && finding.Excerpt.Length <= 60 && !finding.Excerpt.Any(char.IsControl))).IsTrue();
    }

    [Test]
    public async Task LocalTermsUseWholeTokensAndMinimumLength()
    {
        var share = Share() with { Code = "ALICE alice_1 xalice alice2 (Alice) abc" };
        var result = share.FindPrivateData([("user", " alice "), ("short", "abc")]);
        await Assert.That(result.TotalCount).IsEqualTo(2);
    }

    [Test]
    public async Task DuplicateLocalTermsPreserveTheFirstKindAndCountEachOccurrenceOnce()
    {
        var share = Share() with { Code = "Share check model and SHARE CHECK MODEL" };
        var result = share.FindPrivateData([
            ("model or project name", "Share check model"),
            ("model or project name", "  SHARE CHECK MODEL  "),
            ("other name", "share check model")
        ]);
        await Assert.That(result.TotalCount).IsEqualTo(2);
        await Assert.That(result.Findings.Count).IsEqualTo(2);
        await Assert.That(result.Findings.All(finding => finding.Kind == "model or project name" && finding.Field == "code")).IsTrue();
        await Assert.That(result.Incomplete).IsFalse();
    }

    [Test]
    public async Task OrdinaryToolTextHasNoFindings()
    {
        var share = Share() with
        {
            Label = "Count matching walls",
            Description = "Count walls whose name contains a supplied label.",
            Parameters = """{"type":"object","title":"Count matching walls","properties":{"label":{"type":"string","title":"Wall name contains","maxLength":100}},"required":["label"],"additionalProperties":false}""",
            Code = """var label = (string)ctx.Args["label"]; return new FilteredElementCollector(ctx.Document).OfClass(typeof(Wall)).Count(wall => wall.Name.Contains(label));""" + "\n\"Width:\\n\""
        };
        await Assert.That(share.FindPrivateData().TotalCount).IsEqualTo(0);
    }

    [Test]
    public async Task FindingsAreCappedAndCounted()
    {
        var result = (Share() with { Code = string.Join("\n", Enumerable.Repeat("https://example.com/" + new string('a', 100), 80)) }).FindPrivateData();
        await Assert.That(result.TotalCount).IsEqualTo(80);
        await Assert.That(result.Findings.Count).IsEqualTo(50);
        await Assert.That(result.Findings.All(finding => finding.Excerpt.Length == 60)).IsTrue();
    }

    [Test]
    [Arguments("backslashes")]
    [Arguments("hyphens")]
    [Arguments("exclamation_marks")]
    [Arguments("drive_paths")]
    [Arguments("unc_paths")]
    public async Task PathologicalTextScanReturnsPromptly(string scenario)
    {
        const int length = 256 * 1024;
        var text = scenario switch
        {
            "backslashes" => new string('\\', length) + "a",
            "hyphens" => new string('-', length),
            "exclamation_marks" => new string('!', length),
            "drive_paths" => string.Concat(Enumerable.Repeat(@"a:\", length / 3 + 1))[..length],
            _ => string.Concat(Enumerable.Repeat(@"\\a", length / 3 + 1))[..length]
        };
        var share = Share() with { Code = text };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = share.FindPrivateData();
        stopwatch.Stop();
        Console.WriteLine($"Private-data scan {scenario}: {stopwatch.Elapsed.TotalMilliseconds:F3} ms; incomplete={result.Incomplete}");
        await Assert.That(stopwatch.Elapsed < TimeSpan.FromSeconds(5)).IsTrue();
    }

    [Test]
    public async Task OrdinaryFindingsCompleteTheScan()
    {
        var result = (Share() with { Code = @"C:\Projects\A.rvt https://example.com user@example.com" }).FindPrivateData();
        await Assert.That(result.TotalCount).IsEqualTo(3);
        await Assert.That(result.Findings.Select(finding => finding.Kind).Distinct().Count()).IsEqualTo(3);
        await Assert.That(result.Incomplete).IsFalse();
    }

    [Test]
    public async Task ApprovalAndSharingTypesRemainInternal()
    {
        await Assert.That(typeof(ToolShare).IsPublic).IsFalse();
        await Assert.That(typeof(ToolStore).IsPublic).IsFalse();
        await Assert.That(typeof(ToolDefinition).IsPublic).IsFalse();
    }
}
