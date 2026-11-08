namespace RevitModelMcp.Core.Export;

public static class ViewImageFiles
{
    public static TResult Export<TResult>(string targetPath, Action exportImage, Func<string, TResult> readImage)
    {
        if (targetPath is null) throw new ArgumentNullException(nameof(targetPath));
        if (exportImage is null) throw new ArgumentNullException(nameof(exportImage));
        if (readImage is null) throw new ArgumentNullException(nameof(readImage));
        var directory = Path.GetDirectoryName(targetPath)!;
        var fileName = Path.GetFileName(targetPath);
        if (File.Exists(targetPath))
            throw new IOException($"The export file already exists: {fileName}.");
        var pattern = $"{Path.GetFileNameWithoutExtension(targetPath)}*.png";
        var existingFiles = Directory.GetFiles(directory, pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completed = false;
        try
        {
            exportImage();
            // Revit appends the view name to the export prefix.
            var exportedPath = Directory.GetFiles(directory, pattern)
                .Where(path => !existingFiles.Contains(path))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
                ?? throw new IOException("Revit completed the export, but no PNG file was found.");
            if (!string.Equals(exportedPath, targetPath, StringComparison.OrdinalIgnoreCase))
                File.Move(exportedPath, targetPath);
            var result = readImage(targetPath);
            completed = true;
            return result;
        }
        finally
        {
            if (!completed)
            {
                foreach (var path in Directory.GetFiles(directory, pattern).Where(path => !existingFiles.Contains(path)))
                    File.Delete(path);
            }
        }
    }
}
