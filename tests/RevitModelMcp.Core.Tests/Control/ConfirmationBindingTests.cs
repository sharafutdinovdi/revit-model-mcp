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
        await Assert.That(arguments).IsNotEqualTo(DocumentConfirmationBinding.CodeArguments("return 1;", "group"));
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
    [Arguments("name")]
    [Arguments("directory")]
    [Arguments("added")]
    [Arguments("dry_run")]
    [Arguments("overwrite")]
    [Arguments("format")]
    public async Task ExportToken_RejectsChangedPlanAndAcceptsOriginal(string change)
    {
        var tokens = new DocumentConfirmationTokens();
        string[] original = [@"C:\Out\A.pdf"];
        var arguments = DocumentConfirmationBinding.ExportArguments("export", original, "pdf", true, false);
        var changedPaths = change switch
        {
            "name" => new[] { @"C:\Out\B.pdf" },
            "directory" => [@"C:\Other\A.pdf"],
            "added" => [@"C:\Out\A.pdf", @"C:\Out\B.pdf"],
            _ => original
        };
        var changedArguments = DocumentConfirmationBinding.ExportArguments("export", changedPaths,
            change == "format" ? "dwg" : "pdf", change != "overwrite", change == "dry_run");
        var token = tokens.Issue("export", "document", arguments, "state");
        await Assert.That(tokens.Consume(token, "export", "document", changedArguments, "state"))
            .IsEqualTo(DocumentConfirmationResult.ArgumentsMismatch);
        var originalToken = tokens.Issue("export", "document", arguments, "state");
        await Assert.That(tokens.Consume(originalToken, "export", "document", arguments, "state"))
            .IsEqualTo(DocumentConfirmationResult.Valid);
    }

    [Test]
    [Arguments("views")]
    [Arguments("order")]
    [Arguments("combine")]
    [Arguments("pdf")]
    [Arguments("dwg")]
    [Arguments("ifc")]
    [Arguments("csv")]
    public async Task ExportToken_BindsResolvedTargetsAndContentOptions(string change)
    {
        var format = change is "views" or "order" or "combine" ? "pdf" : change;
        var request = new FileExportJob { Format = format, Overwrite = true };
        string[] paths = [@"C:\Out\A." + format];
        var arguments = request.ConfirmationArguments(paths, [1, 2], false);
        var tokens = new DocumentConfirmationTokens();
        var token = tokens.Issue("export", "document", arguments, "state");
        long[] targetIds = change switch
        {
            "views" => [1, 3],
            "order" => [2, 1],
            _ => [1, 2]
        };
        switch (change)
        {
            case "combine": request.Options.Combine = false; break;
            case "pdf": request.Options.Color = "grayscale"; break;
            case "dwg": request.Options.MergedViews = true; break;
            case "ifc": request.Options.ExportBaseQuantities = false; break;
            case "csv": request.Options.Delimiter = ";"; break;
        }
        await Assert.That(tokens.Consume(token, "export", "document",
            request.ConfirmationArguments(paths, targetIds, false), "state"))
            .IsEqualTo(DocumentConfirmationResult.ArgumentsMismatch);
        var unchangedToken = tokens.Issue("export", "document", arguments, "state");
        var original = new FileExportJob { Format = format, Overwrite = true };
        await Assert.That(tokens.Consume(unchangedToken, "export", "document",
            original.ConfirmationArguments(paths, [1, 2], false), "state"))
            .IsEqualTo(DocumentConfirmationResult.Valid);
    }

    [Test]
    [Arguments("pdf")]
    [Arguments("dwg")]
    [Arguments("ifc")]
    [Arguments("csv")]
    public async Task ExportToken_NormalizesExplicitOptionDefaults(string format)
    {
        var request = new FileExportJob { Format = format, Overwrite = true };
        string[] paths = [@"C:\Out\A." + format];
        var arguments = request.ConfirmationArguments(paths, [1], false);
        var tokens = new DocumentConfirmationTokens();
        var token = tokens.Issue("export", "document", arguments, "state");
        request.Options = new FileExportOptions
        {
            Combine = true, Color = "color", ZoomPercent = 100, Paper = "auto",
            Naming = "sheet_number_name", HideCropBoundaries = true, HideScopeBoxes = true,
            MergedViews = false, Version = "IFC2x3CV2", ExportBaseQuantities = true,
            SpaceBoundaries = 0, SplitWallsByLevel = false, Delimiter = ",",
            Headers = true, Title = false, GroupHeaders = false, Encoding = "utf-8"
        };
        await Assert.That(tokens.Consume(token, "export", "document",
            request.ConfirmationArguments(paths, [1], false), "state"))
            .IsEqualTo(DocumentConfirmationResult.Valid);
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

    [Test]
    public async Task DeleteArguments_IgnoreOrderAndDuplicates()
    {
        await Assert.That(DocumentConfirmationBinding.DeleteArguments([3, 1, 2, 2], 600))
            .IsEqualTo(DocumentConfirmationBinding.DeleteArguments([1, 2, 3], 600));
    }

    [Test]
    public async Task DeleteArguments_BindIdsAndDependentCount()
    {
        var baseline = DocumentConfirmationBinding.DeleteArguments([1, 2], 600);
        await Assert.That(DocumentConfirmationBinding.DeleteArguments([1, 3], 600)).IsNotEqualTo(baseline);
        await Assert.That(DocumentConfirmationBinding.DeleteArguments([1, 2], 601)).IsNotEqualTo(baseline);
    }

    [Test]
    public async Task DeleteArguments_DifferFromOtherCommands()
    {
        var arguments = DocumentConfirmationBinding.DeleteArguments([1, 2], 600);
        await Assert.That(arguments).IsNotEqualTo(DocumentConfirmationBinding.ExportArguments("delete", ["1", "2"]));
        await Assert.That(arguments).IsNotEqualTo(DocumentConfirmationBinding.CodeArguments("1,2", "auto"));
    }
}
