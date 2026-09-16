using System.Diagnostics;
using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Serialization;

public static class CommandResponseJsonSerializer
{
    public static string Serialize<T>(CommandResponse<T> response)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var serializer = new DataContractJsonSerializer(
            typeof(CommandResponse<T>),
            new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, response);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public static class CommandResponseJsonFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static string CreatePath(string directory, DateTime localTime, string command, string? correlationId = null)
    {
        var timestamp = localTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var safeCommand = new string(command.Where(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeCommand)) safeCommand = "invalid";
        if (!string.IsNullOrEmpty(correlationId))
            return Path.Combine(directory, $"response_{timestamp}_{safeCommand}_{Uri.EscapeDataString(correlationId)}.json");

        var suffix = string.Empty;
        var counter = 0;
        string path;
        do
        {
            path = Path.Combine(directory, $"response_{timestamp}_{safeCommand}{suffix}.json");
            suffix = $"_{++counter:00}";
        }
        while (File.Exists(path));
        return path;
    }

    public static void Execute(string path, string command, Action<Stopwatch> operation, string? correlationId = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            operation(stopwatch);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Write(
                path,
                CommandResponse<object>.Fail(
                    command,
                    $"Failed to execute the command: {exception}",
                    stopwatch.ElapsedMilliseconds,
                    correlationId));
        }
    }

    public static void Write<T>(string path, CommandResponse<T> response)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("No directory was specified for the response file.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, CommandResponseJsonSerializer.Serialize(response), Utf8WithoutBom);
            const int maximumAttempts = 10;
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                try
                {
#if NET48
                    if (File.Exists(path))
                        File.Replace(temporaryPath, path, null);
                    else
                        File.Move(temporaryPath, path);
#else
                    File.Move(temporaryPath, path, overwrite: true);
#endif
                    break;
                }
                catch (Exception exception) when (attempt < maximumAttempts && exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(attempt);
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
