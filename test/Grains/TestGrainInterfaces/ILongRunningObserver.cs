#nullable enable
using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces;

/// <summary>
/// Observer interface that supports long-running operations with cancellation.
/// </summary>
public interface ILongRunningObserver : IGrainObserver
{
    /// <summary>
    /// Performs a long wait that can be cancelled via the provided cancellation token.
    /// </summary>
    Task LongWait(TimeSpan delay, Guid callId, CancellationToken cancellationToken);

    /// <summary>
    /// Tests that cancellation token callbacks execute in the correct execution context.
    /// </summary>
    Task<bool> CancellationTokenCallbackResolve(Guid callId, CancellationToken cancellationToken);

    /// <summary>
    /// Performs a long wait that can be cancelled via the provided cancellation token.
    /// This method is marked as AlwaysInterleave to allow it to execute concurrently with other requests.
    /// </summary>
    [AlwaysInterleave]
    Task InterleavingLongWait(TimeSpan delay, Guid callId, CancellationToken cancellationToken);
}

/// <summary>
/// Grain interface that can notify observers with cancellation support.
/// </summary>
public interface IObserverWithCancellationGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Subscribes an observer for notifications.
    /// </summary>
    Task Subscribe(ILongRunningObserver observer);

    /// <summary>
    /// Unsubscribes an observer from notifications.
    /// </summary>
    Task Unsubscribe(ILongRunningObserver observer);

    /// <summary>
    /// Notifies the observer to perform a long wait with cancellation support.
    /// </summary>
    Task NotifyLongWait(TimeSpan delay, Guid callId, CancellationToken cancellationToken);

    /// <summary>
    /// Notifies the observer to test cancellation token callback execution context.
    /// </summary>
    Task<bool> NotifyCancellationTokenCallbackResolve(Guid callId, CancellationToken cancellationToken);

    /// <summary>
    /// Notifies the observer to perform an interleaving long wait with cancellation support.
    /// </summary>
    [AlwaysInterleave]
    Task NotifyInterleavingLongWait(TimeSpan delay, Guid callId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the list of cancellations that have been processed by the observer.
    /// </summary>
    Task<List<(Guid CallId, Exception? Error)>> GetProcessedCancellations();

    /// <summary>
    /// Clears the list of processed cancellations.
    /// </summary>
    Task ClearProcessedCancellations();
}
