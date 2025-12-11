#nullable enable
namespace UnitTests.GrainInterfaces;

/// <summary>
/// System target interface for testing CancellationToken propagation and handling.
/// Note: All SystemTarget calls are interleaving by default - they execute immediately without queueing.
/// </summary>
public interface ICancellationTestSystemTarget : ISystemTarget
{
    /// <summary>
    /// Gets the runtime instance identifier for the silo hosting this system target.
    /// </summary>
    Task<string> GetRuntimeInstanceId();

    /// <summary>
    /// Performs a long wait that can be cancelled via the provided cancellation token.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="delay">The delay to wait.</param>
    /// <param name="callId">A unique identifier for this call, used to track cancellations.</param>
    Task LongWait(CancellationToken cancellationToken, TimeSpan delay, Guid callId);

    /// <summary>
    /// Calls another system target's LongWait method, passing through the cancellation token.
    /// </summary>
    /// <param name="target">The target system target to call.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="delay">The delay to wait.</param>
    /// <param name="callId">A unique identifier for this call, used to track cancellations.</param>
    Task CallOtherLongRunningTask(ICancellationTestSystemTarget target, CancellationToken cancellationToken, TimeSpan delay, Guid callId);

    /// <summary>
    /// Calls another system target's LongWait method with a locally created cancellation token
    /// that is cancelled after a specified delay.
    /// </summary>
    /// <param name="target">The target system target to call.</param>
    /// <param name="delay">The delay for the long wait.</param>
    /// <param name="delayBeforeCancel">The delay before cancelling.</param>
    /// <param name="callId">A unique identifier for this call, used to track cancellations.</param>
    Task CallOtherLongRunningTaskWithLocalCancellation(ICancellationTestSystemTarget target, TimeSpan delay, TimeSpan delayBeforeCancel, Guid callId);

    /// <summary>
    /// Tests that cancellation token callbacks execute in the correct execution context.
    /// Returns true if the callback ran on the correct TaskScheduler.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="callId">A unique identifier for this call, used to track cancellations.</param>
    Task<bool> CancellationTokenCallbackResolve(CancellationToken cancellationToken, Guid callId);

    /// <summary>
    /// Calls another system target's CancellationTokenCallbackResolve method with a locally created
    /// cancellation token that is cancelled after a delay.
    /// </summary>
    /// <param name="target">The target system target to call.</param>
    /// <param name="callId">A unique identifier for this call, used to track cancellations.</param>
    Task<bool> CallOtherCancellationTokenCallbackResolve(ICancellationTestSystemTarget target, Guid callId);

    /// <summary>
    /// Tests that exceptions thrown in cancellation callbacks do not propagate.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="callId">A unique identifier for this call, used to track cancellations.</param>
    Task CancellationTokenCallbackThrow(CancellationToken cancellationToken, Guid callId);

    /// <summary>
    /// Checks if a specific call was cancelled.
    /// </summary>
    /// <param name="callId">The call identifier to check.</param>
    /// <returns>True if the call was cancelled, false otherwise.</returns>
    Task<bool> WasCallCancelled(Guid callId);

    /// <summary>
    /// Waits for a specific call to be cancelled.
    /// </summary>
    /// <param name="callId">The call identifier to wait for.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <returns>A tuple containing whether the call was cancelled and any error that occurred.</returns>
    Task<(bool WasCancelled, Exception? Error)> WaitForCancellation(Guid callId, TimeSpan timeout);
}
