using System.Globalization;
using System.IO;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Output;

internal sealed class CommandResponseFileWriter
{
    private readonly string _path;
    private readonly ResponderInfo _responder;

    private CommandResponseFileWriter(string path, ResponderInfo responder)
    {
        _path = path;
        _responder = responder;
    }

    public string FilePath => _path;

    public static CommandResponseFileWriter Create(
        DateTime localTime,
        string command,
        ResponderInfo responder)
    {
        var directory = SnapshotFileWriter.OutputDirectory;
        var timestamp = localTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var safeCommand = new string(command.Where(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeCommand))
        {
            safeCommand = "invalid";
        }

        var suffix = string.Empty;
        var counter = 0;
        string path;
        do
        {
            path = System.IO.Path.Combine(directory, $"response_{timestamp}_{safeCommand}{suffix}.json");
            suffix = $"_{++counter:00}";
        }
        while (File.Exists(path));

        return new CommandResponseFileWriter(path, responder);
    }

    public void Write<T>(CommandResponse<T> response)
    {
        try
        {
            response.Responder = _responder;
            CommandResponseJsonFile.Write(_path, response);
            var outcome = response.Success ? "success" : response.Partial ? "partial" : "error";
            PluginLog.Info(
                $"Response written. Command='{response.Command}'. Outcome='{outcome}'. " +
                $"ElapsedMs={response.ElapsedMs}. Path='{_path}'. Message='{response.Message ?? string.Empty}'.");
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Response write failed. Command='{response.Command}'. Path='{_path}'.", exception);
            throw;
        }
    }

    public void Log(string command, string state, int processed, int total, long elapsedMs, string? currentView)
    {
        var safeView = currentView?.Replace('\r', ' ').Replace('\n', ' ');
        PluginLog.Info(
            $"Progress. Command='{command}'. State='{state}'. Processed={processed}. " +
            $"Total={total}. ElapsedMs={elapsedMs}. CurrentView='{safeView ?? string.Empty}'.");
    }
}
