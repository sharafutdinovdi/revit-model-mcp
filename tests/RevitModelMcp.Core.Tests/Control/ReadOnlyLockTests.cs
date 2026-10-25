using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class ReadOnlyLockTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "model-mcp-lock-" + Guid.NewGuid().ToString("N"));
    private string LockPath => Path.Combine(_folder, "read-only");

    [Test]
    public async Task FilePresentAndAbsent()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        await Assert.That(service.IsLocked).IsFalse();
        File.WriteAllText(LockPath, "");
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
    public async Task ExternalFileChangeNotifiesWithinOneSecond()
    {
        using var service = new ReadOnlyLock(LockPath, () => false);
        var changed = new TaskCompletionSource<ReadOnlyLockState>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += state => { if (state.IsLocked) changed.TrySetResult(state); };
        File.WriteAllText(LockPath, "manual lock");
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
        await Assert.That(preview.ConfirmationText).IsEqualTo("Turn off read-only for all Revit versions of Alice on DESKTOP? The assistant will be able to change models.");
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

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
