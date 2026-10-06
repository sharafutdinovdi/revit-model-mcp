using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class DrainGateTests
{
    [Test]
    public async Task ClosedGateRejectsHandlers()
    {
        var gate = new DrainGate();
        gate.Close();
        await Assert.That(gate.TryEnter()).IsNull();
        await Assert.That(gate.IsClosed).IsTrue();
    }

    [Test]
    public async Task IdleGateDrainsImmediately()
    {
        await Assert.That(new DrainGate().WaitDrained(TimeSpan.Zero)).IsTrue();
    }

    [Test]
    public async Task ShutdownWaitsForSleepingHandler()
    {
        var gate = new DrainGate();
        var scope = gate.TryEnter()!;
        gate.Close();
        await Assert.That(gate.WaitDrained(TimeSpan.FromMilliseconds(100))).IsFalse();
        await Assert.That(gate.Active).IsEqualTo(1);
        var handler = Task.Run(async () =>
        {
            await Task.Delay(100);
            scope.Dispose();
        });
        await Assert.That(gate.WaitDrained(TimeSpan.FromSeconds(5))).IsTrue();
        await handler;
        await Assert.That(gate.Active).IsEqualTo(0);
    }

    [Test]
    public async Task ScopeDisposalIsIdempotent()
    {
        var gate = new DrainGate();
        var scope = gate.TryEnter()!;
        scope.Dispose();
        scope.Dispose();
        await Assert.That(gate.Active).IsEqualTo(0);
    }
}
