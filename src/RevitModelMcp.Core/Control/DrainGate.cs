using System.Diagnostics;

namespace RevitModelMcp.Core.Control;

public sealed class DrainGate
{
    private readonly object _sync = new();
    private int _active;
    private bool _closed;

    public int Active { get { lock (_sync) return _active; } }
    public bool IsClosed { get { lock (_sync) return _closed; } }

    public IDisposable? TryEnter()
    {
        lock (_sync)
        {
            if (_closed) return null;
            _active++;
            return new Scope(this);
        }
    }

    public void Close()
    {
        lock (_sync) _closed = true;
    }

    public bool WaitDrained(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        lock (_sync)
        {
            while (_active > 0)
            {
                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) return false;
                Monitor.Wait(_sync, remaining);
            }
            return true;
        }
    }

    private void Leave()
    {
        lock (_sync)
        {
            _active--;
            if (_active == 0) Monitor.PulseAll(_sync);
        }
    }

    private sealed class Scope(DrainGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Leave();
        }
    }
}
