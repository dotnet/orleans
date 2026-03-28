#nullable enable
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

/// <summary>
/// Grain that manages observers with cancellation support for testing purposes.
/// </summary>
public class ObserverWithCancellationGrain : Grain, IObserverWithCancellationGrain
{
    private readonly List<(Guid CallId, Exception? Error)> _processedCancellations = [];
    private ILongRunningObserver? _observer;

    /// <inheritdoc />
    public Task Subscribe(ILongRunningObserver observer)
    {
        _observer = observer;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Unsubscribe(ILongRunningObserver observer)
    {
        if (ReferenceEquals(_observer, observer))
        {
            _observer = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task NotifyLongWait(TimeSpan delay, Guid callId, CancellationToken cancellationToken)
    {
        if (_observer is null)
        {
            throw new InvalidOperationException("No observer subscribed.");
        }

        try
        {
            await _observer.LongWait(delay, callId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _processedCancellations.Add((callId, null));
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> NotifyCancellationTokenCallbackResolve(Guid callId, CancellationToken cancellationToken)
    {
        if (_observer is null)
        {
            throw new InvalidOperationException("No observer subscribed.");
        }

        try
        {
            return await _observer.CancellationTokenCallbackResolve(callId, cancellationToken);
        }
        finally
        {
            _processedCancellations.Add((callId, null));
        }
    }

    /// <inheritdoc />
    public async Task NotifyInterleavingLongWait(TimeSpan delay, Guid callId, CancellationToken cancellationToken)
    {
        if (_observer is null)
        {
            throw new InvalidOperationException("No observer subscribed.");
        }

        try
        {
            await _observer.InterleavingLongWait(delay, callId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _processedCancellations.Add((callId, null));
            throw;
        }
    }

    /// <inheritdoc />
    public Task<List<(Guid CallId, Exception? Error)>> GetProcessedCancellations()
    {
        return Task.FromResult(_processedCancellations.ToList());
    }

    /// <inheritdoc />
    public Task ClearProcessedCancellations()
    {
        _processedCancellations.Clear();
        return Task.CompletedTask;
    }
}
