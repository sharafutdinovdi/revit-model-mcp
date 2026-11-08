using RevitModelMcp.Core.Export;

namespace RevitModelMcp.Core.Tests.Export;

public sealed class ViewImageFilesTests
{
    [Test]
    public async Task Export_RemovesRenamedPngWhenReadingFails()
    {
        var directory = CreateDirectory();
        try
        {
            var target = Path.Combine(directory, "view.png");
            var generated = Path.Combine(directory, "view - Floor plan.png");
            var existing = Path.Combine(directory, "view_previous.png");
            File.WriteAllText(existing, "existing");
            var failure = new InvalidDataException("Invalid PNG.");
            Exception? reported = null;
            try
            {
                ViewImageFiles.Export<int>(target, () => File.WriteAllText(generated, "invalid"), _ => throw failure);
            }
            catch (InvalidDataException exception) { reported = exception; }
            await Assert.That(reported).IsEqualTo(failure);
            await Assert.That(File.Exists(target)).IsFalse();
            await Assert.That(File.Exists(generated)).IsFalse();
            await Assert.That(File.ReadAllText(existing)).IsEqualTo("existing");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Export_RemovesPngWrittenBeforeExportThrows()
    {
        var directory = CreateDirectory();
        try
        {
            var target = Path.Combine(directory, "view.png");
            var generated = Path.Combine(directory, "view - Floor plan.png");
            var failure = new InvalidOperationException("Export failed.");
            Exception? reported = null;
            try
            {
                ViewImageFiles.Export(target, () =>
                {
                    File.WriteAllText(generated, "partial");
                    throw failure;
                }, File.ReadAllText);
            }
            catch (InvalidOperationException exception) { reported = exception; }
            await Assert.That(reported).IsEqualTo(failure);
            await Assert.That(Directory.GetFiles(directory).Length).IsEqualTo(0);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Export_PreservesSuccessfulImage()
    {
        var directory = CreateDirectory();
        try
        {
            var target = Path.Combine(directory, "view.png");
            var generated = Path.Combine(directory, "view - Floor plan.png");
            var result = ViewImageFiles.Export(target, () => File.WriteAllText(generated, "image"), File.ReadAllText);
            await Assert.That(result).IsEqualTo("image");
            await Assert.That(File.ReadAllText(target)).IsEqualTo("image");
            await Assert.That(File.Exists(generated)).IsFalse();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Export_RefusesExistingTargetWithoutChangingIt()
    {
        var directory = CreateDirectory();
        try
        {
            var target = Path.Combine(directory, "view.png");
            File.WriteAllText(target, "existing");
            var exported = false;
            Exception? reported = null;
            try { ViewImageFiles.Export(target, () => exported = true, File.ReadAllText); }
            catch (IOException exception) { reported = exception; }
            await Assert.That(reported is IOException).IsTrue();
            await Assert.That(exported).IsFalse();
            await Assert.That(File.ReadAllText(target)).IsEqualTo("existing");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "view-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
