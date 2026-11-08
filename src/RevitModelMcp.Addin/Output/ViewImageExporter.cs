using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Export;
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
        var directory = ChannelDirectory.OutputDirectory;
        Directory.CreateDirectory(directory);
        var id = RevitValueReader.GetId(view.Id);
        var stamp = localTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var fileName = $"view_{stamp}_{id}.png";
        var targetPath = Path.Combine(directory, fileName);
        using var options = CreateOptions(view, targetPath, pixelSize, zoomToFit);
        return ViewImageFiles.Export(targetPath, () => document.ExportImage(options), path =>
        {
            var (width, height) = ReadPngSize(path);
            return new ViewExportData
            {
                FileName = fileName,
                Width = width,
                Height = height,
                SizeBytes = new FileInfo(path).Length,
                ViewName = view.Name,
                ViewType = view.ViewType.ToString()
            };
        });
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
            // Horizontal fitting gives a predictable size for views without an Outline.
            return true;
        }
    }

    private static void EnsureSupported(View view)
    {
        if (view.ViewType is ViewType.Schedule or ViewType.Legend || !view.CanBePrinted)
        {
            throw new InvalidOperationException(
                $"View '{view.Name}' has type {view.ViewType}, which cannot be exported as an image.");
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
            throw new InvalidDataException("Revit created a file with an invalid PNG header.");
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
