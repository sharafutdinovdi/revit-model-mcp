using System.Globalization;
using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Control;

[DataContract]
public sealed record ReadOnlyLockState
{
    [DataMember(Name = "enabled")] public bool IsLocked { get; init; }
    [DataMember(Name = "source")] public string Source { get; init; } = "user";
    [DataMember(Name = "setBy")] public string? LockedBy { get; init; }
    [DataMember(Name = "setAtUtc")] public string? LockedAtUtc { get; init; }

    public string Refusal => Source == "policy"
        ? "Read-only is enforced by workstation policy; ask your administrator."
        : $"Read-only is on for this Windows user (set from {LockedBy} at {LockedAtUtc}). Turn it off with the Model MCP ribbon button or by asking the assistant.";
}

public sealed class ReadOnlyLock : IDisposable
{
    private readonly string _path;
    private readonly Func<bool> _policyLocked;
    private readonly object _syncRoot = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private ReadOnlyLockState _state = new();

    public ReadOnlyLock(string path, Func<bool> policyLocked)
    {
        _path = path;
        _policyLocked = policyLocked;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.Error += (_, _) => Refresh();
        _watcher.EnableRaisingEvents = true;
        Refresh();
        _timer = new Timer(_ => Refresh(), null, 500, 500);
    }

    public event Action<ReadOnlyLockState>? Changed;

    public ReadOnlyLockState State { get { Refresh(); lock (_syncRoot) return _state; } }
    public bool IsLocked => State.IsLocked;
    public string? LockedAtUtc => State.LockedAtUtc;
    public string? LockedBy => State.LockedBy;
    public string Source => State.Source;

    public void Set(bool enabled, string setBy)
    {
        if (setBy is not ("ribbon" or "chat")) throw new ArgumentException("Expected ribbon or chat.", nameof(setBy));
        lock (_syncRoot)
        {
            if (_policyLocked()) throw new InvalidOperationException(new ReadOnlyLockState { Source = "policy" }.Refusal);
            if (enabled)
                File.WriteAllLines(_path, [setBy, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)]);
            else
                File.Delete(_path);
        }
        Refresh();
    }

    public ActionResultData SetFromChat(bool enabled, string? token, DocumentConfirmationTokens tokens,
        string user, string machine)
    {
        var state = State;
        if (state.Source == "policy") throw new InvalidOperationException(state.Refusal);
        if (!enabled)
        {
            var identity = $"{user}@{machine}";
            var binding = $"{state.IsLocked}:{state.LockedBy}:{state.LockedAtUtc}";
            if (token is null)
                return new ActionResultData
                {
                    NeedsConfirmation = true,
                    ConfirmationText = "Turn off read-only for all Revit versions of this Windows user on this PC? The assistant will be able to change models.",
                    ConfirmToken = tokens.Issue("set-read-only", identity, "false", binding),
                    Summary = "Needs confirmation to turn off read-only."
                };
            if (tokens.Consume(token, "set-read-only", identity, "false", binding) != DocumentConfirmationResult.Valid)
                throw new InvalidOperationException("Confirmation token is unknown, expired, already used, or the lock changed. Repeat the call without confirm_token.");
        }
        Set(enabled, "chat");
        return new ActionResultData { Summary = enabled ? "Read-only turned on for this Windows user." : "Read-only turned off for this Windows user." };
    }

    public void Refresh()
    {
        ReadOnlyLockState current;
        lock (_syncRoot)
        {
            current = ReadState();
            if (current == _state) return;
            _state = current;
        }
        Changed?.Invoke(current);
    }

    private ReadOnlyLockState ReadState()
    {
        if (_policyLocked()) return new ReadOnlyLockState { IsLocked = true, Source = "policy" };
        try
        {
            if (Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path)).Length == 0) return new();
            var lines = File.ReadAllLines(_path);
            var metadata = lines.Length == 2 && lines[0] is "ribbon" or "chat" &&
                           DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
            return new ReadOnlyLockState
            {
                IsLocked = true,
                LockedBy = metadata ? lines[0] : "file",
                LockedAtUtc = metadata ? lines[1] : File.GetLastWriteTimeUtc(_path).ToString("O", CultureInfo.InvariantCulture)
            };
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (IOException) { return new() { IsLocked = true, LockedBy = "file", LockedAtUtc = "unknown" }; }
        catch (UnauthorizedAccessException) { return new() { IsLocked = true, LockedBy = "file", LockedAtUtc = "unknown" }; }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs arguments) => Refresh();

    public void Dispose()
    {
        _timer.Dispose();
        _watcher.Dispose();
    }
}
