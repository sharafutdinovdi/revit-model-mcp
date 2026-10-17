using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Activity;

namespace RevitModelMcp.Activity;

/// <summary>
/// In-memory ring buffer of the last 500 recorded MCP jobs, also appended as JSON lines to
/// <c>%LOCALAPPDATA%\RevitModelMcp\activity.log</c> (or <c>activity.log</c> under the policy LogDir). <see cref="ActivityEntry.Document"/> always holds
/// <c>Document.Title</c> (a file name, never a directory), so nothing here needs path redaction.
/// </summary>
internal static class ActivityLog
{
    private static readonly DataContractJsonSerializer Serializer = new(typeof(ActivityEntry));
    private const int MaxEntries = 500;
    private const long FileSizeLimitBytes = 5 * 1024 * 1024;
    private static readonly object SyncRoot = new();
    private static readonly LinkedList<ActivityEntry> Entries = new();
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static string LogDirectory => Policy.PolicyProvider.Current.LogDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitModelMcp");
    private static string LogPath => Path.Combine(LogDirectory, "activity.log");
    private static string RotatedLogPath => Path.Combine(LogDirectory, "activity.1.log");

    public static event Action? Changed;

    public static ActivityEntry Record(ActivityEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
        lock (SyncRoot)
        {
            Entries.AddFirst(entry);
            while (Entries.Count > MaxEntries) Entries.RemoveLast();
        }
        AppendToFile(entry);
        Changed?.Invoke();
        return entry;
    }

    public static IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (SyncRoot) return Entries.ToList();
    }

    public static ActivityEntry? Newest()
    {
        lock (SyncRoot) return Entries.First?.Value;
    }

    public static ActivityEntry? NewestUndoable()
    {
        lock (SyncRoot) return UndoEligibility.NewestUndoable(Entries);
    }

    /// <summary>Marks the most recent activity entry whose recorded undo entry name matches as undone.</summary>
    public static bool MarkUndoneByEntryName(string undoEntryName)
    {
        ActivityEntry? match;
        lock (SyncRoot)
        {
            match = Entries.FirstOrDefault(entry => !entry.Undone &&
                string.Equals(entry.UndoEntryName, undoEntryName, StringComparison.Ordinal));
            if (match is not null) match.Undone = true;
        }
        if (match is null) return false;
        Changed?.Invoke();
        return true;
    }

    private static void AppendToFile(ActivityEntry entry)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            using var stream = new MemoryStream();
            Serializer.WriteObject(stream, entry);
            var line = Encoding.UTF8.GetString(stream.ToArray());
            lock (SyncRoot)
            {
                try
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > FileSizeLimitBytes)
                    {
                        if (File.Exists(RotatedLogPath)) File.Delete(RotatedLogPath);
                        File.Move(LogPath, RotatedLogPath);
                    }
                }
                catch (IOException exception) { PluginLog.Error("Activity log rotation failed.", exception); }
                catch (UnauthorizedAccessException exception) { PluginLog.Error("Activity log rotation failed.", exception); }
                File.AppendAllText(LogPath, line + Environment.NewLine, Utf8WithoutBom);
            }
        }
        catch (IOException exception) { PluginLog.Error("Activity log append failed.", exception); }
        catch (UnauthorizedAccessException exception) { PluginLog.Error("Activity log append failed.", exception); }
    }
}
