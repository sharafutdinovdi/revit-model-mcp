using System.Diagnostics;

namespace RevitModelMcp.Core.Control;

public enum BodyReadStatus { Complete, TooLarge, TimedOut }

public sealed record BodyReadResult(BodyReadStatus Status, byte[] Body);

public static class BoundedBodyReader
{
    public static async Task<BodyReadResult> ReadAsync(Stream stream, long maxBytes, TimeSpan deadline, CancellationToken cancellation)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var elapsed = Stopwatch.StartNew();
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = deadline - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return new(BodyReadStatus.TimedOut, []);
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellation);
            var delay = Task.Delay(remaining, delayCancellation.Token);
            var completed = await Task.WhenAny(readTask, delay).ConfigureAwait(false);
            delayCancellation.Cancel();
            if (completed != readTask)
            {
                _ = readTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                cancellation.ThrowIfCancellationRequested();
                return new(BodyReadStatus.TimedOut, []);
            }
            cancellation.ThrowIfCancellationRequested();
            var read = await readTask.ConfigureAwait(false);
            if (read == 0) return new(BodyReadStatus.Complete, body.ToArray());
            if (body.Length + read > maxBytes) return new(BodyReadStatus.TooLarge, []);
            body.Write(buffer, 0, read);
        }
    }
}
