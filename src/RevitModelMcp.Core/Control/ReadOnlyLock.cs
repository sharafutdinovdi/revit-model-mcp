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

    internal string? Revision { get; init; }

    public string Refusal => Source == "unavailable"
        ? "Workstation locking is unavailable. Changes are refused. Check local application data permissions and restart Revit."
        : Source == "policy"
        ? "Read-only is enforced by workstation policy; ask your administrator."
        : $"Read-only is on for this Windows user (set from {LockedBy} at {LockedAtUtc}). Turn it off with the Model MCP ribbon button or by asking the assistant.";
}

public sealed class ReadOnlyLock : IDisposable
{
    private readonly string _path;
    private readonly Func<bool> _policyLocked;
    private readonly Action? _beforeUnlock;
    private readonly object _syncRoot = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _timer;
    private ReadOnlyLockState _state = new();

    public ReadOnlyLock(string path, Func<bool> policyLocked)
        : this(path, policyLocked, (directory, filter) => new FileSystemWatcher(directory, filter))
    {
    }

    internal ReadOnlyLock(string path, Func<bool> policyLocked, Action? beforeUnlock)
        : this(path, policyLocked, (directory, filter) => new FileSystemWatcher(directory, filter), beforeUnlock)
    {
    }

    internal ReadOnlyLock(string path, Func<bool> policyLocked, Func<string, string, FileSystemWatcher> createWatcher,
        Action? beforeUnlock = null)
    {
        _path = path;
        _policyLocked = policyLocked;
        _beforeUnlock = beforeUnlock;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            _watcher = createWatcher(directory, Path.GetFileName(path));
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime;
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.Error += (_, _) => Refresh();
            _watcher.EnableRaisingEvents = true;
            Refresh();
            _timer = new Timer(_ => Refresh(), null, 500, 500);
        }
        catch (Exception exception)
        {
            InitializationError = $"Workstation lock initialization failed ({exception.GetType().Name}). " +
                                  "Changes are refused. Check local application data permissions and restart Revit.";
            _watcher?.Dispose();
            _timer?.Dispose();
            Refresh();
        }
    }

    public string? InitializationError { get; }

    public event Action<ReadOnlyLockState>? Changed;

    public ReadOnlyLockState State { get { Refresh(); lock (_syncRoot) return _state; } }
    public bool IsLocked => State.IsLocked;
    public string? LockedAtUtc => State.LockedAtUtc;
    public string? LockedBy => State.LockedBy;
    public string Source => State.Source;

    public void Set(bool enabled, string setBy)
    {
        if (setBy is not ("ribbon" or "chat")) throw new ArgumentException("Expected ribbon or chat.", nameof(setBy));
        if (InitializationError is not null) throw new InvalidOperationException(State.Refusal);
        lock (_syncRoot)
        {
            using var guard = AcquireWriteGuard();
            if (_policyLocked()) throw new InvalidOperationException(new ReadOnlyLockState { Source = "policy" }.Refusal);
            if (enabled)
                WriteState(setBy);
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
            var binding = $"{state.IsLocked}:{state.LockedBy}:{state.LockedAtUtc}:{state.Revision}";
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
            if (InitializationError is not null) throw new InvalidOperationException(State.Refusal);
            _beforeUnlock?.Invoke();
            lock (_syncRoot)
            {
                using var guard = AcquireWriteGuard();
                var current = ReadState();
                if (current.Source == "policy") throw new InvalidOperationException(current.Refusal);
                if (current != state)
                    throw new InvalidOperationException("The read-only lock changed. Repeat the call without confirm_token.");
                File.Delete(_path);
            }
            Refresh();
        }
        else
            Set(true, "chat");
        return new ActionResultData { Summary = enabled ? "Read-only turned on for this Windows user." : "Read-only turned off for this Windows user." };
    }

    public void Refresh()
    {
        ReadOnlyLockState current;
        lock (_syncRoot)
        {
            current = InitializationError is null ? ReadState() : new ReadOnlyLockState { IsLocked = true, Source = "unavailable" };
            if (current == _state) return;
            _state = current;
        }
        Changed?.Invoke(current);
    }

    private void WriteState(string setBy)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, [setBy, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N")]);
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string[] ReadLines()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is string line) lines.Add(line);
                return lines.ToArray();
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) == 32 && attempt < 9)
            {
                Thread.Sleep(10);
            }
        }
    }

    private FileStream AcquireWriteGuard()
    {
        // The guard file is permanent; deleting it would allow concurrent owners of different file handles.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_path + ".guard", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(10);
            }
        }
    }

    private ReadOnlyLockState ReadState()
    {
        if (_policyLocked()) return new ReadOnlyLockState { IsLocked = true, Source = "policy" };
        try
        {
            if (Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path)).Length == 0) return new();
            var lines = ReadLines();
            var metadata = lines.Length is 2 or 3 && lines[0] is "ribbon" or "chat" &&
                           DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
            return new ReadOnlyLockState
            {
                IsLocked = true,
                LockedBy = metadata ? lines[0] : "file",
                LockedAtUtc = metadata ? lines[1] : File.GetLastWriteTimeUtc(_path).ToString("O", CultureInfo.InvariantCulture),
                Revision = string.Join("\n", lines)
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
        _timer?.Dispose();
        _watcher?.Dispose();
    }
}
