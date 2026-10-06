using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ConfirmationBindingTests
{
    [Test]
    public async Task LinkRemovalArguments_AreOrderIndependentAndBindEachLink()
    {
        var links = new (string Kind, long Id, string Name)[]
        {
            ("revit", 2, "Structure"), ("cad", 3, "Site"), ("revit", 1, "Architecture"),
            ("revit", 1, "Alternate")
        };
        var arguments = DocumentConfirmationBinding.LinkRemovalArguments(links);
        await Assert.That(arguments).IsEqualTo(DocumentConfirmationBinding.LinkRemovalArguments(links.AsEnumerable().Reverse()));
        await Assert.That(arguments).IsEqualTo("links:cad:3:Site\nrevit:1:Alternate\nrevit:1:Architecture\nrevit:2:Structure");
        var changedLinks = new (string Kind, long Id, string Name)[]
        {
            ("image", 2, "Structure"), ("revit", 4, "Structure"), ("revit", 2, "Changed")
        };
        foreach (var changed in changedLinks)
        {
            await Assert.That(arguments).IsNotEqualTo(
                DocumentConfirmationBinding.LinkRemovalArguments(links.Skip(1).Append(changed)));
        }
    }

    [Test]
    public async Task CodeArguments_BindCodeAndTransactionMode()
    {
        var arguments = DocumentConfirmationBinding.CodeArguments("return 1;", "auto");
        await Assert.That(arguments).IsEqualTo(DocumentConfirmationBinding.CodeArguments("return 1;", "auto"));
        await Assert.That(arguments).IsNotEqualTo(DocumentConfirmationBinding.CodeArguments("return 2;", "auto"));
        await Assert.That(arguments).IsNotEqualTo(DocumentConfirmationBinding.CodeArguments("return 1;", "none"));
        await Assert.That(arguments).IsEqualTo($"code:{DocumentConfirmationBinding.CodeHash("return 1;")}:9:auto");
    }

    [Test]
    public async Task CodeHash_IsStableLowercaseSha256()
    {
        var hash = DocumentConfirmationBinding.CodeHash("abc");
        await Assert.That(hash.Length).IsEqualTo(64);
        await Assert.That(hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')).IsTrue();
        await Assert.That(hash).IsEqualTo(DocumentConfirmationBinding.CodeHash("abc"));
        await Assert.That(hash).IsEqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Test]
    public async Task ExportArguments_AreOrderIndependentAndNormalizePaths()
    {
        var arguments = DocumentConfirmationBinding.ExportArguments("export", [@"C:\Out\B.ifc", @"C:\Out\A.ifc"]);
        await Assert.That(arguments).IsEqualTo(
            DocumentConfirmationBinding.ExportArguments("export", [@"c:\out\a.ifc\", @"c:\out\b.ifc"]));
        await Assert.That(arguments).IsNotEqualTo(
            DocumentConfirmationBinding.ExportArguments("export-nwc", [@"C:\Out\B.ifc", @"C:\Out\A.ifc"]));
        await Assert.That(arguments).IsNotEqualTo(
            DocumentConfirmationBinding.ExportArguments("export", [@"C:\Out\C.ifc", @"C:\Out\A.ifc"]));
    }

    [Test]
    public async Task FileState_ReportsMissingAndBindsLengthAndModificationTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"confirmation-{Guid.NewGuid():N}.tmp");
        try
        {
            await Assert.That(DocumentConfirmationBinding.FileState([path])).IsEqualTo("missing");
            File.WriteAllText(path, "a");
            var initialTime = File.GetLastWriteTimeUtc(path);
            var initial = DocumentConfirmationBinding.FileState([path]);
            await Assert.That(initial).IsEqualTo($"1:{initialTime.Ticks}");
            File.WriteAllText(path, "longer");
            File.SetLastWriteTimeUtc(path, initialTime);
            var resized = DocumentConfirmationBinding.FileState([path]);
            await Assert.That(resized).IsNotEqualTo(initial);
            File.SetLastWriteTimeUtc(path, initialTime.AddMinutes(1));
            var modified = DocumentConfirmationBinding.FileState([path]);
            await Assert.That(modified).IsNotEqualTo(resized);
            await Assert.That(DocumentConfirmationBinding.FileState([path, path + ".missing"]))
                .IsEqualTo(DocumentConfirmationBinding.FileState([path + ".missing", path]));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Tokens_RejectChangedArgumentsAndStateAndCannotBeReused()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var tokens = new DocumentConfirmationTokens(() => now);
        var arguments = DocumentConfirmationBinding.CodeArguments("return 1;", "auto");
        var changedArguments = DocumentConfirmationBinding.CodeArguments("return 2;", "auto");
        var token = tokens.Issue("execute-code", "model", arguments, "state");
        await Assert.That(tokens.Consume(token, "execute-code", "model", changedArguments, "state"))
            .IsEqualTo(DocumentConfirmationResult.ArgumentsMismatch);
        await Assert.That(tokens.Consume(token, "execute-code", "model", arguments, "state"))
            .IsEqualTo(DocumentConfirmationResult.Invalid);
        token = tokens.Issue("execute-code", "model", arguments, "state");
        await Assert.That(tokens.Consume(token, "execute-code", "model", arguments, "changed"))
            .IsEqualTo(DocumentConfirmationResult.DocumentChanged);
        await Assert.That(tokens.Consume(token, "execute-code", "model", arguments, "state"))
            .IsEqualTo(DocumentConfirmationResult.Invalid);
        token = tokens.Issue("execute-code", "model", arguments, "state");
        await Assert.That(tokens.Consume(token, "execute-code", "model", arguments, "state"))
            .IsEqualTo(DocumentConfirmationResult.Valid);
        await Assert.That(tokens.Consume(token, "execute-code", "model", arguments, "state"))
            .IsEqualTo(DocumentConfirmationResult.Invalid);
    }

    [Test]
    public async Task Tokens_ExpireAtFiveMinutes()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var tokens = new DocumentConfirmationTokens(() => now);
        var token = tokens.Issue("export", "model", "targets", "state");
        now = now.AddMinutes(5);
        await Assert.That(tokens.Consume(token, "export", "model", "targets", "state"))
            .IsEqualTo(DocumentConfirmationResult.Invalid);
    }
}
