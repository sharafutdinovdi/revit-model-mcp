using System.Globalization;
using System.IO;
using System.Text;

namespace RevitModelMcp;

internal static class PluginLog
{
    private const long FileSizeLimitBytes = 10 * 1024 * 1024;
    private const int RetainedFileCountLimit = 14;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly object SyncRoot = new();
    private static string? _directory;

    public static string FilePath => ResolveFilePath(DateTime.Now);

    public static void Start()
    {
        lock (SyncRoot)
        {
            _directory = ResolveLogDirectory();
            DeleteExpiredFiles(_directory);
        }

        Info($"Logging started. PluginVersion='{typeof(PluginLog).Assembly.GetName().Version}'.");
    }

    public static void Info(string message)
    {
        Write("INF", message, null);
    }

    public static void Warn(string message)
    {
        Write("WRN", message, null);
    }

    public static void Error(string message, Exception exception)
    {
        Write("ERR", message, exception);
    }

    public static void Shutdown()
    {
        Info("Logging stopped.");
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (SyncRoot)
            {
                _directory ??= ResolveLogDirectory();
                var path = ResolveFilePath(DateTime.Now);
                path = RotateBySize(path);
                var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
                var line = $"{timestamp} [{level}] {Sanitize(message)}";
                if (exception is not null)
                {
                    line += $"{Environment.NewLine}{exception}";
                }

                File.AppendAllText(path, line + Environment.NewLine, Utf8WithoutBom);
            }
        }
        catch
        {
            // Diagnostic failures must not escape into Revit or interrupt the main response.
        }
    }

    private static string ResolveFilePath(DateTime now)
    {
        var directory = _directory ?? ResolveLogDirectory();
        return Path.Combine(directory, $"RevitModelMcp-{now:yyyyMMdd}.log");
    }

    private static string RotateBySize(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < FileSizeLimitBytes)
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var index = 1;
        string rotatedPath;
        do
        {
            rotatedPath = Path.Combine(directory, $"{fileName}_{index++:000}.log");
        }
        while (File.Exists(rotatedPath) && new FileInfo(rotatedPath).Length >= FileSizeLimitBytes);

        return rotatedPath;
    }

    private static string ResolveLogDirectory()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RevitModelMcp",
                "Logs");
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch
        {
            // The fallback preserves diagnostics when Documents is temporarily unavailable.
            var fallback = Path.Combine(Path.GetTempPath(), "RevitModelMcp", "Logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static void DeleteExpiredFiles(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "RevitModelMcp-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(RetainedFileCountLimit))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // Logging must continue even if rotation fails.
        }
    }

    private static string Sanitize(string message)
    {
        return (message ?? string.Empty)
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }
}
