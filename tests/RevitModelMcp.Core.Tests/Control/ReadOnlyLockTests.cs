using System.Diagnostics;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;
using TUnit.Core.Enums;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ReadOnlyLockTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "model-mcp-lock-" + Guid.NewGuid().ToString("N"));
    private string LockPath => Path.Combine(_folder, "read-only");

    private void WriteLockFile(string content)
    {
        using var guard = new FileStream(LockPath + ".guard", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var temporary = LockPath + ".tmp";
        File.WriteAllText(temporary, content);
        if (File.Exists(LockPath)) File.Replace(temporary, LockPath, null);
        else File.Move(temporary, LockPath);
    }

    private string ReadLockFile()
    {
        using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Test]
    public async Task OpenReaderAllowsReplacementAndUnlock()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        service.Set(true, "chat");
        using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        service.Set(true, "ribbon");
        await Assert.That(service.LockedBy).IsEqualTo("ribbon");
        await Assert.That(reader.ReadLine()).IsEqualTo("chat");
        service.Set(false, "ribbon");
        await Assert.That(service.IsLocked).IsFalse();
    }

    [Test]
    public async Task ReadersAllowConcurrentWriter()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        service.Set(true, "chat");
        using var writer = new FileStream(LockPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await Assert.That(service.LockedBy).IsEqualTo("chat");
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task SharingViolationRetriesUntilReaderCanOpen()
    {
        Directory.CreateDirectory(_folder);
        WriteLockFile(string.Join(Environment.NewLine, "ribbon", DateTimeOffset.UtcNow.ToString("O")));
        using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var service = new ReadOnlyLock(LockPath, () => false);
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            release.Wait();
            Thread.Sleep(30);
            stream.Dispose();
        });
        owner.Start();
        try
        {
            release.Set();
            await Assert.That(service.LockedBy).IsEqualTo("ribbon");
            await Assert.That(service.LockedAtUtc).IsNotEqualTo("unknown");
        }
        finally
        {
            release.Set();
            owner.Join();
        }
    }

    [Test]
    public async Task PersistentSharingViolationFailsClosed()
    {
        Directory.CreateDirectory(_folder);
        WriteLockFile("manual lock");
        using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var service = new ReadOnlyLock(LockPath, () => false);
        await Assert.That(service.IsLocked).IsTrue();
        await Assert.That(service.LockedBy).IsEqualTo("file");
        await Assert.That(service.LockedAtUtc).IsEqualTo("unknown");
    }

    [Test]
    public async Task FilePresentAndAbsent()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        await Assert.That(service.IsLocked).IsFalse();
        WriteLockFile("");
        await Assert.That(service.IsLocked).IsTrue();
        await Assert.That(service.LockedBy).IsEqualTo("file");
        await Assert.That(service.LockedAtUtc).IsNotNull();
        File.Delete(LockPath);
        await Assert.That(service.IsLocked).IsFalse();
    }

    [Test]
    [Arguments("ribbon")]
    [Arguments("chat")]
    public async Task MetadataAndRefusal(string source)
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        service.Set(true, source);
        using var other = new ReadOnlyLock(LockPath, () => false);
        await Assert.That(other.Source).IsEqualTo("user");
        await Assert.That(other.LockedBy).IsEqualTo(source);
        await Assert.That(other.State.Refusal).IsEqualTo(
            $"Read-only is on for this Windows user (set from {source} at {service.LockedAtUtc}). Turn it off with the Model MCP ribbon button or by asking the assistant.");
        other.Set(false, source);
        await Assert.That(service.IsLocked).IsFalse();
    }

    [Test]
    public async Task PolicyWinsAndCannotBeChanged()
    {
        var policy = false;
        using var service = new ReadOnlyLock(LockPath, () => policy);
        service.Set(true, "ribbon");
        policy = true;
        await Assert.That(service.IsLocked).IsTrue();
        await Assert.That(service.Source).IsEqualTo("policy");
        await Assert.That(service.State.Refusal).IsEqualTo("Read-only is enforced by workstation policy; ask your administrator.");
        foreach (var enabled in new[] { true, false })
        {
            await Assert.That(() => service.Set(enabled, "ribbon")).Throws<InvalidOperationException>();
            await Assert.That(() => service.SetFromChat(enabled, null, new(), "user", "machine")).Throws<InvalidOperationException>();
        }
        policy = false;
        await Assert.That(service.LockedBy).IsEqualTo("ribbon");
    }

    [Test]
    [NotInParallel]
    public async Task ExternalFileChangeNotifiesWithinOneSecond()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        var changed = new TaskCompletionSource<ReadOnlyLockState>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += state => { if (state.IsLocked) changed.TrySetResult(state); };
        WriteLockFile("manual lock");
        await Assert.That(await Task.WhenAny(changed.Task, Task.Delay(1000)) == changed.Task).IsTrue();
        await Assert.That((await changed.Task).LockedBy).IsEqualTo("file");
        await Assert.That((await changed.Task).Refusal).Contains("set from file at ");
    }

    [Test]
    public async Task ChatLocksImmediatelyAndUnlockNeedsSingleUseToken()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        var tokens = new DocumentConfirmationTokens();
        var locked = service.SetFromChat(true, null, tokens, "Alice", "DESKTOP");
        await Assert.That(service.IsLocked).IsTrue();
        await Assert.That(service.LockedBy).IsEqualTo("chat");
        await Assert.That(locked.Summary).Contains("turned on");
        var preview = service.SetFromChat(false, null, tokens, "Alice", "DESKTOP");
        await Assert.That(preview.NeedsConfirmation).IsTrue();
        await Assert.That(preview.ConfirmationText).IsEqualTo("Turn off read-only for all Revit versions of this Windows user on this PC? The assistant will be able to change models.");
        await Assert.That(service.IsLocked).IsTrue();
        var unlocked = service.SetFromChat(false, preview.ConfirmToken, tokens, "Alice", "DESKTOP");
        await Assert.That(service.IsLocked).IsFalse();
        await Assert.That(unlocked.Summary).Contains("turned off");
        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "Alice", "DESKTOP")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ExpiredOrChangedConfirmationDoesNotUnlock()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        var now = DateTimeOffset.UtcNow;
        var tokens = new DocumentConfirmationTokens(() => now);
        service.Set(true, "ribbon");
        var preview = service.SetFromChat(false, null, tokens, "user", "machine");
        now = now.AddMinutes(5);
        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine")).Throws<InvalidOperationException>();
        await Assert.That(service.IsLocked).IsTrue();
        preview = service.SetFromChat(false, null, tokens, "user", "machine");
        service.Set(true, "chat");
        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine")).Throws<InvalidOperationException>();
        await Assert.That(service.IsLocked).IsTrue();
    }

    [Test]
    public async Task ReplacementAfterTokenValidationSurvivesUnlock()
    {
        using var writer = new ReadOnlyLock(LockPath, () => false);
        using var service = new ReadOnlyLock(LockPath, () => false, () => writer.Set(true, "ribbon"));
        var tokens = new DocumentConfirmationTokens();
        service.Set(true, "chat");
        var preview = service.SetFromChat(false, null, tokens, "user", "machine");

        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine"))
            .Throws<InvalidOperationException>().WithMessage("The read-only lock changed. Repeat the call without confirm_token.");
        await Assert.That(writer.IsLocked).IsTrue();
        await Assert.That(writer.LockedBy).IsEqualTo("ribbon");
        var replacement = ReadLockFile();
        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine"))
            .Throws<InvalidOperationException>();
        await Assert.That(ReadLockFile()).IsEqualTo(replacement);
    }

    [Test]
    public async Task ReplacementWithIdenticalMetadataInvalidatesConfirmation()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        WriteLockFile(string.Join(Environment.NewLine, "ribbon", timestamp, Guid.NewGuid().ToString("N")));
        var tokens = new DocumentConfirmationTokens();
        var preview = service.SetFromChat(false, null, tokens, "user", "machine");
        WriteLockFile(string.Join(Environment.NewLine, "ribbon", timestamp, Guid.NewGuid().ToString("N")));
        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine"))
            .Throws<InvalidOperationException>();
        await Assert.That(service.IsLocked).IsTrue();
        await Assert.That(service.LockedBy).IsEqualTo("ribbon");
    }

    [Test]
    public async Task PolicyEnabledAfterValidationPreventsUnlock()
    {
        var policy = false;
        using var service = new ReadOnlyLock(LockPath, () => policy, () => policy = true);
        service.Set(true, "ribbon");
        var tokens = new DocumentConfirmationTokens();
        var preview = service.SetFromChat(false, null, tokens, "user", "machine");
        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine"))
            .Throws<InvalidOperationException>().WithMessage("Read-only is enforced by workstation policy; ask your administrator.");
        await Assert.That(service.Source).IsEqualTo("policy");
        policy = false;
        await Assert.That(service.IsLocked).IsTrue();
    }

    [Test]
    public async Task LegacyMetadataCanBeUnlocked()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        WriteLockFile(string.Join(Environment.NewLine, "ribbon", DateTimeOffset.UtcNow.ToString("O")));
        var tokens = new DocumentConfirmationTokens();
        var preview = service.SetFromChat(false, null, tokens, "user", "machine");
        service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine");
        await Assert.That(service.IsLocked).IsFalse();
    }

    [Test]
    public async Task WritesWaitForGuardAcrossProcesses()
    {
        const string childPathVariable = "MODEL_MCP_LOCK_TEST_PATH";
        var childPath = Environment.GetEnvironmentVariable(childPathVariable);
        if (childPath is not null)
        {
            using var writer = new ReadOnlyLock(childPath, () => false);
            File.WriteAllText(childPath + ".ready", "");
            writer.Set(true, "ribbon");
            return;
        }

        Directory.CreateDirectory(_folder);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--fx-version");
        start.ArgumentList.Add(Environment.Version.ToString());
        start.ArgumentList.Add(typeof(ReadOnlyLockTests).Assembly.Location);
        start.ArgumentList.Add("--treenode-filter");
        start.ArgumentList.Add("/*/*/ReadOnlyLockTests/WritesWaitForGuardAcrossProcesses");
        start.Environment[childPathVariable] = LockPath;
        using var guard = new FileStream(LockPath + ".guard", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(LockPath + ".ready") && !process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(20);
            await Assert.That(File.Exists(LockPath + ".ready")).IsTrue();
            await Task.Delay(200);
            await Assert.That(process.HasExited).IsFalse();
            await Assert.That(File.Exists(LockPath)).IsFalse();
            guard.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var output = await standardOutput + await standardError;
            await Assert.That(process.ExitCode).IsEqualTo(0).Because(output);
            using var service = new ReadOnlyLock(LockPath, () => false);
            await Assert.That(service.IsLocked).IsTrue();
            await Assert.That(service.LockedBy).IsEqualTo("ribbon");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    [Test]
    public async Task ReplacementAfterValidationSurvivesUnlockAcrossProcesses()
    {
        const string childPathVariable = "MODEL_MCP_UNLOCK_REPLACEMENT_TEST_PATH";
        var childPath = Environment.GetEnvironmentVariable(childPathVariable);
        if (childPath is not null)
        {
            using var writer = new ReadOnlyLock(childPath, () => false);
            writer.Set(true, "ribbon");
            return;
        }

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--fx-version");
        start.ArgumentList.Add(Environment.Version.ToString());
        start.ArgumentList.Add(typeof(ReadOnlyLockTests).Assembly.Location);
        start.ArgumentList.Add("--treenode-filter");
        start.ArgumentList.Add("/*/*/ReadOnlyLockTests/ReplacementAfterValidationSurvivesUnlockAcrossProcesses");
        start.Environment[childPathVariable] = LockPath;
        using var process = new Process { StartInfo = start };
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        string? replacement = null;
        using var service = new ReadOnlyLock(LockPath, () => false, () =>
        {
            process.Start();
            standardOutput = process.StandardOutput.ReadToEndAsync();
            standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                process.Kill(true);
                process.WaitForExit();
                throw new TimeoutException("The replacement writer did not exit.");
            }
            replacement = ReadLockFile();
        });
        service.Set(true, "chat");
        var original = ReadLockFile();
        var tokens = new DocumentConfirmationTokens();
        var preview = service.SetFromChat(false, null, tokens, "user", "machine");

        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine"))
            .Throws<InvalidOperationException>().WithMessage("The read-only lock changed. Repeat the call without confirm_token.");
        var output = await standardOutput! + await standardError!;
        await Assert.That(process.ExitCode).IsEqualTo(0).Because(output);
        await Assert.That(replacement).IsNotEqualTo(original);
        await Assert.That(ReadLockFile()).IsEqualTo(replacement);
        await Assert.That(service.IsLocked).IsTrue();
        await Assert.That(service.LockedBy).IsEqualTo("ribbon");
    }

    [Test]
    public async Task PingSerializesLockFields()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        service.Set(true, "chat");
        var response = ReadCommandResponseFactory.Ping(0);
        response.ReadOnly = service.State;
        var json = CommandResponseJsonSerializer.Serialize(response);
        await Assert.That(json).Contains("\"readOnly\":{");
        await Assert.That(json).Contains("\"enabled\":true");
        await Assert.That(json).Contains("\"source\":\"user\"");
        await Assert.That(json).Contains("\"setBy\":\"chat\"");
        await Assert.That(json).Contains("\"setAtUtc\":");
    }

    [Test]
    public async Task HealthPayloadSerializesLockFieldsWithoutTypeMetadata()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        var json = ControlPayloadJsonSerializer.Serialize(new Dictionary<string, object> { ["readOnly"] = service.State });
        await Assert.That(json).Contains("\"enabled\":false");
        await Assert.That(json).Contains("\"setBy\":null");
        await Assert.That(json).Contains("\"setAtUtc\":null");
    }

    [Test]
    public async Task DirectoryInitializationFailureKeepsWritesRefusedAfterDirectoryRecovery()
    {
        Directory.CreateDirectory(_folder);
        var obstruction = Path.Combine(_folder, "blocked");
        File.WriteAllText(obstruction, "");
        using var service = new ReadOnlyLock(Path.Combine(obstruction, "read-only"), () => false);
        await Assert.That(service.InitializationError).Contains("IOException");
        File.Delete(obstruction);
        Directory.CreateDirectory(obstruction);
        await AssertInitializationFailure(service);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WatcherInitializationFailureKeepsWritesRefused(bool accessDenied)
    {
        using var service = new ReadOnlyLock(LockPath, () => false, (_, _) => accessDenied
            ? throw new UnauthorizedAccessException("confidential path")
            : throw new IOException("confidential path"));
        await Assert.That(service.InitializationError).Contains(accessDenied ? "UnauthorizedAccessException" : "IOException");
        await Assert.That(service.InitializationError).DoesNotContain("confidential path");
        await AssertInitializationFailure(service);
    }

    [Test]
    public async Task WatcherActivationFailureKeepsWritesRefused()
    {
        using var service = new ReadOnlyLock(LockPath, () => false, (directory, filter) =>
        {
            var watcher = new FileSystemWatcher(directory, filter);
            watcher.Dispose();
            return watcher;
        });
        await Assert.That(service.InitializationError).IsNotNull();
        await AssertInitializationFailure(service);
    }

    private static async Task AssertInitializationFailure(ReadOnlyLock service)
    {
        service.Refresh();
        await Assert.That(service.IsLocked).IsTrue();
        await Assert.That(service.Source).IsEqualTo("unavailable");
        await Assert.That(service.State.Refusal).Contains("restart Revit");
        foreach (var enabled in new[] { true, false })
            await Assert.That(() => service.Set(enabled, "ribbon")).Throws<InvalidOperationException>();
        var tokens = new DocumentConfirmationTokens();
        await Assert.That(() => service.SetFromChat(true, null, tokens, "user", "machine")).Throws<InvalidOperationException>();
        var preview = service.SetFromChat(false, null, tokens, "user", "machine");
        await Assert.That(preview.NeedsConfirmation).IsTrue();
        await Assert.That(preview.ConfirmToken).IsNotNull();
        await Assert.That(() => service.SetFromChat(false, preview.ConfirmToken, tokens, "user", "machine")).Throws<InvalidOperationException>();
        await Assert.That(service.IsLocked).IsTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
