using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Output;

internal static class ViewImageExporter
{
    public static ViewExportData Export(
        Document document,
        View view,
        int pixelSize,
        bool zoomToFit,
        DateTime localTime)
    {
        EnsureSupported(view);
        var directory = SnapshotFileWriter.OutputDirectory;
        Directory.CreateDirectory(directory);
        var id = RevitValueReader.GetId(view.Id);
        var stamp = localTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var fileName = $"view_{stamp}_{id}.png";
        var targetPath = Path.Combine(directory, fileName);
        if (File.Exists(targetPath))
        {
            throw new IOException($"Файл экспорта уже существует: {fileName}.");
        }

        var prefix = Path.GetFileNameWithoutExtension(targetPath);
        var existingFiles = Directory.GetFiles(directory, $"{prefix}*.png")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var options = CreateOptions(view, targetPath, pixelSize, zoomToFit);
        document.ExportImage(options);

        // Revit добавляет к префиксу имя вида, поэтому итоговый файл находим после экспорта.
        var exportedPath = Directory.GetFiles(directory, $"{prefix}*.png")
            .Where(path => !existingFiles.Contains(path))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new IOException("Revit завершил экспорт, но PNG-файл не найден.");
        if (!string.Equals(exportedPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(exportedPath, targetPath);
        }

        var (width, height) = ReadPngSize(targetPath);
        return new ViewExportData
        {
            FileName = fileName,
            Width = width,
            Height = height,
            SizeBytes = new FileInfo(targetPath).Length,
            ViewName = view.Name,
            ViewType = view.ViewType.ToString()
        };
    }

    private static ImageExportOptions CreateOptions(
        View view,
        string targetPath,
        int pixelSize,
        bool zoomToFit)
    {
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = targetPath,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            PixelSize = pixelSize,
            ImageResolution = ImageResolution.DPI_150
        };
        options.SetViewsAndSheets(new List<ElementId> { view.Id });
        if (zoomToFit)
        {
            options.ZoomType = ZoomFitType.FitToPage;
            options.FitDirection = IsLandscape(view)
                ? FitDirectionType.Horizontal
                : FitDirectionType.Vertical;
        }
        else
        {
            options.ZoomType = ZoomFitType.Zoom;
            options.Zoom = 100;
        }

        return options;
    }

    private static bool IsLandscape(View view)
    {
        try
        {
            var outline = view.Outline;
            return outline.Max.U - outline.Min.U >= outline.Max.V - outline.Min.V;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            // Для редких видов без Outline горизонталь даёт предсказуемый размер.
            return true;
        }
    }

    private static void EnsureSupported(View view)
    {
        if (view.ViewType is ViewType.Schedule or ViewType.Legend || !view.CanBePrinted)
        {
            throw new InvalidOperationException(
                $"Вид «{view.Name}» имеет тип {view.ViewType}, который нельзя экспортировать в изображение.");
        }
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        var header = new byte[24];
        using var stream = File.OpenRead(path);
        var read = 0;
        while (read < header.Length)
        {
            var count = stream.Read(header, read, header.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read != header.Length ||
            !header.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            throw new InvalidDataException("Revit создал файл с неверным заголовком PNG.");
        }

        return (
            ReadBigEndianInt32(header, 16),
            ReadBigEndianInt32(header, 20));
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset) =>
        bytes[offset] << 24 |
        bytes[offset + 1] << 16 |
        bytes[offset + 2] << 8 |
        bytes[offset + 3];
}
