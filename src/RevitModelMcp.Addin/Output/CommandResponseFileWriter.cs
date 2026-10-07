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
            ChannelDirectory.OutputDirectory, localTime, command, correlationId);
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
            var loggedMessage = response.Command == "execute-code" ? "[omitted]" : response.Message ?? string.Empty;
            PluginLog.Info(
                $"Response written. Command='{response.Command}'. Outcome='{outcome}'. " +
                $"ElapsedMs={response.ElapsedMs}. Path='{_path}'. Message='{loggedMessage}'.");
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Response write failed. Command='{response.Command}'. Path='{_path}'.", exception);
            var message = $"Response delivery failed ({exception.GetType().Name}).";
            var failure = CommandResponse<object>.Fail(response.Command, message, response.ElapsedMs, _correlationId);
            failure.Error = message;
            failure.Responder = _responder;
            try
            {
                if (ResponseDelivery.Current is { } delivery)
                    delivery(CommandResponseJsonSerializer.Serialize(failure));
                else
                    CommandResponseJsonFile.Write(_path, failure);
            }
            catch (Exception fallbackException)
            {
                PluginLog.Error($"Fallback response write failed. Command='{response.Command}'. Path='{_path}'.", fallbackException);
                throw;
            }
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
    [ThreadStatic] public static Func<bool>? CancellationRequested;
}
