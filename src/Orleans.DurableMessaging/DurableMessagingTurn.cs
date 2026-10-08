using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

// Accessed on the owning activation. Runtime admission stays non-interleaving and neutral.
// Only the timer/state is reused: every logical arm carries an immutable owner-bound payload.
internal abstract class DurableMessagingTurn<T> : IDisposable where T : struct
{
    private IGrainTimer? _timer;
    private T? _pending;
    private bool _disposed;
    private long _registrationGeneration;
    private CancellationToken _timerCancellation;

    protected abstract IGrainTimer RegisterTimer(long registrationGeneration);
    protected abstract Task ExecuteAsync(T payload, CancellationToken cancellationToken);
    protected abstract void Discard(T payload);

    public void Queue(T payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending is { } superseded)
        {
            _pending = null;
            Discard(superseded);
        }
        _pending = payload;
        try
        {
            // Register disarmed so no callback can observe a partially installed timer handle.
            if (_timerCancellation.IsCancellationRequested)
            {
                // A disposed physical handle is never reanimated. The active callback keeps its
                // original payload/token; only future arms receive a new registration generation.
                _timer?.Dispose();
                _timer = null;
                _timerCancellation = default;
            }
            _timer ??= RegisterTimer(++_registrationGeneration);
            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        catch
        {
            _pending = null; // Queue's caller still owns this failed admission and its cleanup.
            throw;
        }
    }

    public Task RunAsync(long registrationGeneration, CancellationToken cancellationToken)
    {
        if (_disposed || registrationGeneration != _registrationGeneration || _pending is not { } payload)
        {
            return Task.CompletedTask;
        }
        _pending = null;
        _timerCancellation = cancellationToken;
        // The active payload is a value snapshot, never the mutable slot used by a subsequent arm.
        return ExecuteAsync(payload, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pending is { } queued)
        {
            _pending = null;
            Discard(queued);
        }
        _timer?.Dispose();
    }
}
