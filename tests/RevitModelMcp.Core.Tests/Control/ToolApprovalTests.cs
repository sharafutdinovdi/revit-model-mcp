using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ToolApprovalTests
{
    private static ToolDefinition VectorDefinition() => new()
    {
        Name = "cross_runtime",
        Description = "Count \"walls\" / café\b\f\t\r\n",
        Parameters = """{"type":"object","properties":{"size":{"type":"number","minimum":1.5}}}""",
        Code = "return \"Привет/😀\";\n",
        Mode = "read",
        TestedRevitYears = [2022, 2026],
        Created = "2026-11-10T00:00:00Z",
        Author = "tester/作者",
        Revision = "2f922fa8bfcdbfdfdaf2c247d059831544ccf2a9c2046a048b13f2371dab1f7f"
    };

    [Test]
    [Arguments("en-US")]
    [Arguments("tr-TR")]
    [Arguments("ar-SA")]
    public async Task CanonicalApproval_PinsUtf8PayloadSignatureAndBinding(string culture)
    {
        const string expectedPayload = """{"author":"tester/作者","code":"return \"Привет/😀\";\n","created":"2026-11-10T00:00:00Z","description":"Count \"walls\" / café\u0008\u000c\t\r\n","mode":"read","name":"cross_runtime","parameters":"{\"type\":\"object\",\"properties\":{\"size\":{\"type\":\"number\",\"minimum\":1.5}}}","replacedRevision":"","revision":"2f922fa8bfcdbfdfdaf2c247d059831544ccf2a9c2046a048b13f2371dab1f7f","testedRevitYears":[2022,2026]}""";
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, ".approval-key"), Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
            var definition = VectorDefinition();
            var store = new ToolStore(directory);
            store.Save(definition);
            var payload = File.ReadAllText(Path.Combine(directory, definition.Name, definition.Revision + ".json"));
            await Assert.That(payload).IsEqualTo(expectedPayload);
            await Assert.That(definition.ApprovalPayload()).IsEqualTo(expectedPayload);
            await Assert.That(CodeSource.CodeHash(payload)).IsEqualTo("b16d47f0abe43d4381abcdaef42009a8d8ddc1a3469ce4dc5ab1235b192b67c3");
            await Assert.That(File.ReadAllText(Path.Combine(directory, definition.Name, definition.Revision + ".approved")))
                .IsEqualTo("RDUGBrlyLFk8LhBj6die6l4YoPIAb0SXhDriLBETOMY=");
            await Assert.That(definition.ApprovalBinding()).IsEqualTo("397ba82f69c6ebeb364e46837a70ccf78c6b25ce3eddea1ba0781250648859db");
            await Assert.That(store.Get(definition.Name).ApprovalPayload()).IsEqualTo(expectedPayload);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LegacyApproval_AcceptsOriginalBytesWithEitherControlEscapePolicy(bool unicodeEscapes)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var definition = VectorDefinition();
            var store = new ToolStore(directory);
            store.Save(definition);
            var payload = ToolJson.Serialize(definition);
            if (unicodeEscapes)
                payload = payload.Replace("\\n", "\\u000a").Replace("\\r", "\\u000d").Replace("\\t", "\\u0009")
                    .Replace("\\b", "\\u0008").Replace("\\f", "\\u000c");
            var path = Path.Combine(directory, definition.Name, definition.Revision + ".json");
            File.WriteAllText(path, payload);
            using var signature = new HMACSHA256(File.ReadAllBytes(Path.Combine(directory, ".approval-key")));
            File.WriteAllText(Path.ChangeExtension(path, ".approved"), Convert.ToBase64String(signature.ComputeHash(Encoding.UTF8.GetBytes(payload))));
            var reopened = new ToolStore(directory).Get(definition.Name);
            await Assert.That(reopened.ApprovalPayload()).IsEqualTo(definition.ApprovalPayload());
            await Assert.That(reopened.ApprovalBinding()).IsEqualTo(definition.ApprovalBinding());
            File.WriteAllText(path, payload.Replace("Count", "Changed"));
            await Assert.That(() => store.Get(definition.Name)).Throws<InvalidOperationException>();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    [Arguments("author")]
    [Arguments("created")]
    [Arguments("years")]
    public async Task Approval_StillBindsSavedMetadata(string field)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var definition = VectorDefinition();
            var store = new ToolStore(directory);
            store.Save(definition);
            var changed = field switch
            {
                "author" => definition with { Author = "different" },
                "created" => definition with { Created = "2026-11-11T00:00:00Z" },
                _ => definition with { TestedRevitYears = [2027] }
            };
            File.WriteAllText(Path.Combine(directory, definition.Name, definition.Revision + ".json"), changed.ApprovalPayload());
            await Assert.That(() => store.Get(definition.Name)).Throws<InvalidOperationException>();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public async Task ReformattingStoredJsonRequiresApprovalAgain()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var definition = VectorDefinition();
            var store = new ToolStore(directory);
            store.Save(definition);
            var path = Path.Combine(directory, definition.Name, definition.Revision + ".json");
            File.WriteAllText(path, " " + File.ReadAllText(path));
            await Assert.That(() => store.Get(definition.Name)).Throws<InvalidOperationException>();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
