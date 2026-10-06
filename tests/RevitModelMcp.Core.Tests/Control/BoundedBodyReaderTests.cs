using System.Diagnostics;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class BoundedBodyReaderTests
{
    [Test]
    public async Task CompleteBodyAndEmptyBody()
    {
        byte[][] bodies = [[], [1, 2, 3]];
        foreach (var body in bodies)
        {
            using var stream = new MemoryStream(body);
            var result = await BoundedBodyReader.ReadAsync(stream, 10, TimeSpan.FromSeconds(1), CancellationToken.None);
            await Assert.That(result.Status).IsEqualTo(BodyReadStatus.Complete);
            await Assert.That(result.Body.SequenceEqual(body)).IsTrue();
        }
    }

    [Test]
    public async Task ExactlyMaxBytesIsAccepted()
    {
        using var stream = new MemoryStream(new byte[8192]);
        var result = await BoundedBodyReader.ReadAsync(stream, 8192, TimeSpan.FromSeconds(1), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(BodyReadStatus.Complete);
        await Assert.That(result.Body.Length).IsEqualTo(8192);
    }

    [Test]
    public async Task SmallChunksExceedingLimitAreRejected()
    {
        using var stream = new TrickleStream(11, TimeSpan.Zero);
        var result = await BoundedBodyReader.ReadAsync(stream, 10, TimeSpan.FromSeconds(1), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(BodyReadStatus.TooLarge);
        await Assert.That(result.Body.Length).IsEqualTo(0);
    }

    [Test]
    public async Task StalledReadIgnoresCancellationButStillTimesOut()
    {
        using var stream = new StalledStream();
        var elapsed = Stopwatch.StartNew();
        var result = await BoundedBodyReader.ReadAsync(stream, 10, TimeSpan.FromMilliseconds(150), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(BodyReadStatus.TimedOut);
        await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        stream.Pending.SetException(new IOException("Late read failure."));
    }

    [Test]
    public async Task DeadlineIsTotalAcrossSlowReads()
    {
        using var stream = new TrickleStream(10, TimeSpan.FromMilliseconds(80));
        var elapsed = Stopwatch.StartNew();
        var result = await BoundedBodyReader.ReadAsync(stream, 10, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(BodyReadStatus.TimedOut);
        await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task CallerCancellationThrows()
    {
        using var stream = new StalledStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);
        var cancelled = false;
        try { await BoundedBodyReader.ReadAsync(stream, 10, TimeSpan.FromSeconds(5), cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        await Assert.That(cancelled).IsTrue();
    }

    private sealed class StalledStream : MemoryStream
    {
        public TaskCompletionSource<int> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Pending.Task;
    }

    private sealed class TrickleStream(int bytes, TimeSpan delay) : MemoryStream
    {
        private int _remaining = bytes;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_remaining == 0) return 0;
            await Task.Delay(delay, cancellationToken);
            _remaining--;
            buffer[offset] = 1;
            return 1;
        }
    }
}
