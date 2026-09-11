using System.IO;
using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Output;

namespace RevitModelMcp.Control;

internal sealed class ControlChannel
{
    private readonly string _triggerFilePath;
    private IControlSession? _session;
    private string? _lastSkippedJob;

    public ControlChannel(string triggerFilePath)
    {
        _triggerFilePath = triggerFilePath;
    }

    public bool HasActiveSession => _session is not null;

    public void Tick(UIApplication application)
    {
        if (_session is not null)
        {
            ProcessActiveSession(application);
            return;
        }

        if (!File.Exists(_triggerFilePath))
        {
            _lastSkippedJob = null;
            return;
        }

        string content;
        try
        {
            content = File.ReadAllText(_triggerFilePath);
        }
        catch (IOException)
        {
            // Вызывающая сторона может ещё дописывать файл; резервный таймер повторит чтение.
            return;
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Job read failed. TriggerPath='{_triggerFilePath}'.", exception);
            if (TryDeleteTriggerFile())
            {
                TryWriteError(application, "invalid", $"Не удалось прочитать задание: {exception}", DateTimeOffset.Now);
            }

            return;
        }

        var parsed = ControlJobParser.Parse(content);
        if (!MatchesCurrentInstance(application, parsed))
        {
            LogSkippedOnce(parsed, content);
            return;
        }

        _lastSkippedJob = null;
        if (!TryClaimTriggerFile(application, parsed))
        {
            return;
        }

        var startedAt = DateTimeOffset.Now;
        PluginLog.Info(
            $"Job received. Command='{parsed.Command}'. Parameters='{content}'. TriggerPath='{_triggerFilePath}'.");
        if (parsed.Cause is not null)
        {
            PluginLog.Error($"Job parsing failed. Command='{parsed.Command}'.", parsed.Cause);
        }

        if (parsed.Kind == ControlJobKind.LegacySnapshot)
        {
            PluginLog.Info("Job processing started. Command='legacy-snapshot'.");
            try
            {
                var result = SnapshotService.CaptureAndWrite(application, startedAt);
                PluginLog.Info(
                    $"Job processing finished. Command='legacy-snapshot'. Outcome='{(result.WriteResult.Success ? "success" : "error")}'. " +
                    $"ResponsePath='{result.WriteResult.LatestPath}'. Error='{result.WriteResult.Error ?? string.Empty}'.");
            }
            catch (Exception exception)
            {
                PluginLog.Error("Legacy snapshot failed.", exception);
                TryWriteError(application, "legacy-snapshot", $"Не удалось выполнить команду: {exception}", startedAt);
            }

            return;
        }

        if (parsed.Kind == ControlJobKind.Invalid)
        {
            PluginLog.Info($"Job processing started. Command='{parsed.Command}'.");
            TryWriteError(application, parsed.Command, parsed.Error ?? "Некорректное задание.", startedAt);
            return;
        }

        try
        {
            PluginLog.Info($"Job processing started. Command='{parsed.Command}'.");
            if (parsed.Kind == ControlJobKind.ViewsDump)
            {
                StartSession(new ViewDumpSession(application, parsed.Views, startedAt), application);
            }
            else if (parsed.Kind == ControlJobKind.ViewElements)
            {
                StartSession(new ViewElementsSession(application, parsed, startedAt), application);
            }
            else
            {
                ReadCommandExecutor.Execute(application, parsed, startedAt);
            }
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Job start failed. Command='{parsed.Command}'.", exception);
            TryWriteError(
                application,
                parsed.Command,
                $"Не удалось начать выполнение команды: {exception}",
                startedAt);
            _session = null;
        }
    }

    private void RejectPendingJob(UIApplication application)
    {
        if (!File.Exists(_triggerFilePath))
        {
            return;
        }

        string parameters;
        try
        {
            parameters = File.ReadAllText(_triggerFilePath);
        }
        catch (Exception exception)
        {
            parameters = "<unreadable>";
            PluginLog.Error("Busy job could not be read before rejection.", exception);
        }

        var parsed = ControlJobParser.Parse(parameters);
        if (!MatchesCurrentInstance(application, parsed))
        {
            LogSkippedOnce(parsed, parameters);
            return;
        }

        _lastSkippedJob = null;
        if (TryClaimTriggerFile(application, parsed))
        {
            PluginLog.Warn(
                $"Job rejected while busy. Parameters='{parameters}'. TriggerPath='{_triggerFilePath}'.");
            _session!.RejectJobWhileBusy();
        }
    }

    private void StartSession(IControlSession session, UIApplication application)
    {
        _session = session;
        ProcessActiveSession(application);
    }

    private void ProcessActiveSession(UIApplication application)
    {
        try
        {
            RejectPendingJob(application);
            _session!.ProcessTick(application);
        }
        catch (Exception exception)
        {
            // Последний защитный слой нужен, чтобы сбой сессии не остался без terminal response.
            PluginLog.Error("Active session failed outside its handler.", exception);
            try
            {
                _session!.Abort(exception);
            }
            catch (Exception abortException)
            {
                PluginLog.Error("Active session could not write its terminal response.", abortException);
            }
        }

        if (_session!.IsFinished)
        {
            _session = null;
        }
    }

    private bool TryDeleteTriggerFile()
    {
        try
        {
            File.Delete(_triggerFilePath);
            return true;
        }
        catch (Exception exception)
        {
            // Заблокированный trigger оставляем таймеру: он повторит попытку без внешнего запроса.
            PluginLog.Error($"Trigger delete failed. TriggerPath='{_triggerFilePath}'.", exception);
            return false;
        }
    }

    private bool TryClaimTriggerFile(
        UIApplication application,
        ControlJobParseResult job)
    {
        try
        {
            var document = application.ActiveUIDocument?.Document;
            return JobTargetMatcher.TryClaim(
                _triggerFilePath,
                job,
                document?.Title,
                document?.PathName,
                Process.GetCurrentProcess().Id);
        }
        catch (Exception exception)
        {
            // Заблокированный trigger оставляем таймеру: он повторит попытку без внешнего запроса.
            PluginLog.Error($"Trigger delete failed. TriggerPath='{_triggerFilePath}'.", exception);
            return false;
        }
    }

    public void Shutdown()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _session.Abort(new OperationCanceledException("Revit завершает работу."));
        }
        catch (Exception exception)
        {
            PluginLog.Error("Active session shutdown failed.", exception);
        }
        finally
        {
            _session = null;
        }
    }

    public void HandleUnhandledException(UIApplication application, Exception exception)
    {
        // Даже сбой над обычной защитой Tick обязан закончиться terminal response.
        PluginLog.Error("Unhandled control channel error in ExternalEvent.", exception);
        if (_session is not null)
        {
            try
            {
                _session.Abort(exception);
            }
            catch (Exception abortException)
            {
                PluginLog.Error("Active session could not write its terminal response.", abortException);
            }
            finally
            {
                _session = null;
            }

            return;
        }

        TryWriteError(
            application,
            "invalid",
            $"Обработка задания аварийно завершилась: {exception}",
            DateTimeOffset.Now);
    }

    private static void TryWriteError(
        UIApplication application,
        string command,
        string message,
        DateTimeOffset startedAt)
    {
        try
        {
            ReadCommandExecutor.WriteError(application, command, message, startedAt);
        }
        catch (Exception exception)
        {
            PluginLog.Error($"Fallback response failed. Command='{command}'.", exception);
        }
    }

    private static bool MatchesCurrentInstance(
        UIApplication application,
        ControlJobParseResult job)
    {
        var document = application.ActiveUIDocument?.Document;
        return JobTargetMatcher.Matches(
            job,
            document?.Title,
            document?.PathName,
            Process.GetCurrentProcess().Id);
    }

    private void LogSkippedOnce(ControlJobParseResult job, string content)
    {
        var identity = $"{File.GetLastWriteTimeUtc(_triggerFilePath).Ticks}:{content}";
        if (string.Equals(_lastSkippedJob, identity, StringComparison.Ordinal))
        {
            return;
        }

        _lastSkippedJob = identity;
        PluginLog.Info(
            $"Job skipped for another Revit instance. Command='{job.Command}'. " +
            $"TargetDocument='{job.TargetDocument ?? string.Empty}'. TargetProcessId='{job.TargetProcessId?.ToString() ?? string.Empty}'.");
    }
}

internal interface IControlSession
{
    bool IsFinished { get; }

    void ProcessTick(UIApplication application);

    void RejectJobWhileBusy();

    void Abort(Exception exception);
}
