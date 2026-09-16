using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Output;

internal sealed class CommandResponseFileWriter
{
    private readonly string _path;
    private readonly ResponderInfo _responder;
    private readonly string? _correlationId;

    private CommandResponseFileWriter(string path, ResponderInfo responder, string? correlationId)
    {
        _path = path;
        _responder = responder;
        _correlationId = correlationId;
    }

    public string FilePath => _path;

    public static CommandResponseFileWriter Create(
        DateTime localTime,
        string command,
        ResponderInfo responder,
        string? correlationId = null)
    {
        var path = CommandResponseJsonFile.CreatePath(
            SnapshotFileWriter.OutputDirectory, localTime, command, correlationId);
        return new CommandResponseFileWriter(path, responder, correlationId);
    }

    public void Write<T>(CommandResponse<T> response)
    {
        try
        {
            response.Responder = _responder;
            response.CorrelationId = _correlationId;
            if (ResponseDelivery.Current is { } delivery)
            {
                delivery(CommandResponseJsonSerializer.Serialize(response));
                return;
            }
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

internal static class ResponseDelivery
{
    [ThreadStatic] public static Action<string>? Current;
}
