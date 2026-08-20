using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Journaling;

/// <summary>
/// Observes durable state manager commit and recovery boundaries.
/// </summary>
/// <remarks>
/// <para>
/// Callbacks run synchronously on the owning grain scheduler and must not block. Implementations
/// should queue asynchronous follow-up work instead of performing it inline.
/// </para>
/// <para>
/// Before recovery resets registered state, the manager invokes <see cref="OnRecoveryStarted"/>.
/// After recovery restores all registered state, it invokes <see cref="OnRecoveryCompleted"/>.
/// Before each write, the manager awaits <see cref="OnWritePreparingAsync"/> and then invokes
/// <see cref="OnWriteStarted"/> immediately before capturing registered states. Mutations remain
/// provisional until <see cref="OnWriteCompleted"/> marks a successful commit. Restoring the last
/// durable state, including through <see cref="IJournaledStateManager.RevertPendingChangesAsync"/>,
/// invokes <see cref="OnRecoveryCompleted"/> after all registered states have been restored.
/// </para>
/// Exceptions from <see cref="OnWritePreparingAsync"/> abort the write before state is captured.
/// Exceptions from the synchronous notification methods are logged and don't change the outcome
/// of a completed commit or recovery.
/// Each operation uses one stable snapshot of registered observers for all of its callbacks.
/// Observers registered from a callback begin participating in subsequent operations.
/// </remarks>
public interface IJournaledStateObserver
{
    /// <summary>
    /// Called before registered states are reset for recovery.
    /// </summary>
    void OnRecoveryStarted() { }

    /// <summary>
    /// Prepares external prerequisites for a durable write before registered states are captured.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the preparation operation.</returns>
    /// <remarks>
    /// State mutated by this method is included in the pending write. Throwing aborts the write.
    /// Implementations must be idempotent because a failed storage write can be retried.
    /// </remarks>
    ValueTask OnWritePreparingAsync(CancellationToken cancellationToken) => default;

    /// <summary>
    /// Called immediately before registered states are captured for a durable write.
    /// Mutations made after this callback aren't included in the corresponding
    /// <see cref="OnWriteCompleted"/> boundary.
    /// </summary>
    void OnWriteStarted();

    /// <summary>
    /// Called after all pending journal entries have been committed to stable storage.
    /// </summary>
    void OnWriteCompleted();

    /// <summary>
    /// Called after all registered states have been restored to the last durable version.
    /// </summary>
    void OnRecoveryCompleted();
}
