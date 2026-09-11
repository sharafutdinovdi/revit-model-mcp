using System.Globalization;
using System.IO;
using System.Text;
using RevitModelMcp.Core.Formatting;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Output;

internal sealed class ViewDumpOutput
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private ViewDumpOutput(string jsonPath, string textPath)
    {
        JsonPath = jsonPath;
        TextPath = textPath;
    }

    public string JsonPath { get; }

    public string TextPath { get; }

    public static ViewDumpOutput Create(DateTime localTime)
    {
        var directory = SnapshotFileWriter.OutputDirectory;
        var timestamp = localTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var suffix = string.Empty;
        var counter = 0;
        while (File.Exists(Path.Combine(directory, $"views_dump_{timestamp}{suffix}.json")))
        {
            suffix = $"_{++counter:00}";
        }

        return new ViewDumpOutput(
            Path.Combine(directory, $"views_dump_{timestamp}{suffix}.json"),
            Path.Combine(directory, $"views_dump_{timestamp}{suffix}.txt"));
    }

    public void Write(ViewDumpReport report)
    {
        if (ResponseDelivery.Current is { } delivery)
        {
            delivery(ViewDumpJsonSerializer.Serialize(report));
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(JsonPath)!);
        File.WriteAllText(JsonPath, ViewDumpJsonSerializer.Serialize(report), Utf8WithoutBom);
        File.WriteAllText(TextPath, ViewDumpTextFormatter.Format(report), Utf8WithoutBom);
    }
}
