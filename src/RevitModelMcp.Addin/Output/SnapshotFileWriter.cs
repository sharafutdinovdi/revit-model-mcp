using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using RevitModelMcp.Capture;
using RevitModelMcp.Core.Formatting;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Output;

internal static class SnapshotService
{
    public static SnapshotRunResult CaptureAndWrite(UIApplication uiApplication, DateTimeOffset localNow)
    {
        var snapshot = SnapshotCollector.Capture(uiApplication, localNow);
        var writeResult = SnapshotFileWriter.Write(snapshot, localNow.LocalDateTime);
        return new SnapshotRunResult(snapshot, writeResult);
    }
}

internal sealed class SnapshotRunResult
{
    public SnapshotRunResult(Snapshot snapshot, SnapshotWriteResult writeResult)
    {
        Snapshot = snapshot;
        WriteResult = writeResult;
    }

    public Snapshot Snapshot { get; }

    public SnapshotWriteResult WriteResult { get; }
}

internal static class SnapshotFileWriter
{
    internal static string OutputDirectory => Environment.GetEnvironmentVariable("REVIT_MCP_CHANNEL_DIR") is { Length: > 0 } directory
        ? directory
        : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitModelMcp");

    public static SnapshotWriteResult Write(Snapshot snapshot, DateTime localTime)
    {
        var outputDirectory = OutputDirectory;
        var latestPath = Path.Combine(outputDirectory, "latest.json");
        var historyPath = Path.Combine(outputDirectory, $"snapshot_{localTime:yyyyMMdd_HHmmss}.json");
        var summaryPath = Path.Combine(outputDirectory, "latest.txt");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            var encoding = new UTF8Encoding(false);
            var json = SnapshotJsonSerializer.Serialize(snapshot);
            File.WriteAllText(latestPath, json, encoding);
            File.WriteAllText(historyPath, json, encoding);
            File.WriteAllText(summaryPath, SnapshotSummaryFormatter.Format(snapshot), encoding);
            return SnapshotWriteResult.Succeeded(latestPath);
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Legacy snapshot response write failed. Path='{latestPath}'.", exception);
            return SnapshotWriteResult.Failed(latestPath, exception.Message);
        }
    }
}

internal sealed class SnapshotWriteResult
{
    private SnapshotWriteResult(bool success, string latestPath, string? error)
    {
        Success = success;
        LatestPath = latestPath;
        Error = error;
    }

    public bool Success { get; }

    public string LatestPath { get; }

    public string? Error { get; }

    public static SnapshotWriteResult Succeeded(string latestPath)
    {
        return new SnapshotWriteResult(true, latestPath, null);
    }

    public static SnapshotWriteResult Failed(string latestPath, string error)
    {
        return new SnapshotWriteResult(false, latestPath, error);
    }
}
