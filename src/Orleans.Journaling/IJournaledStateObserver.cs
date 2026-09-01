namespace Orleans.Journaling;

/// <summary>
/// Observes durable state manager commit and recovery boundaries.
/// </summary>
/// <remarks>
/// Each operation uses a stable snapshot of registered observers. Preparation runs before
/// state capture, completion runs after a successful write boundary, and recovery completion
/// runs after every registered state has been restored.
/// </remarks>
public interface IJournaledStateObserver
{
    /// <summary>
    /// Validates a write request before it is queued.
    /// </summary>
    void OnWriteRequested() { }

    /// <summary>
    /// Validates a delete request before it is queued.
    /// </summary>
    void OnDeleteRequested() { }

    /// <summary>
    /// Called when recovery is requested, before it is queued.
    /// </summary>
    void OnRecoveryRequested() { }

    /// <summary>
    /// Called before registered state is reset and replay begins.
    /// </summary>
    void OnRecoveryStarted() { }

    /// <summary>
    /// Prepares external prerequisites before registered states are captured.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the preparation operation.</returns>
    ValueTask OnWritePreparingAsync(CancellationToken cancellationToken) => default;

    /// <summary>
    /// Finalizes prerequisites after every observer has prepared and before state capture begins.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the finalization operation.</returns>
    /// <remarks>
    /// Implementations can validate the fully prepared state and mutate only state they exclusively own.
    /// </remarks>
    ValueTask OnWriteFinalizingAsync(CancellationToken cancellationToken) => default;

    /// <summary>
    /// Validates prerequisites before all journaled state is deleted.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the validation operation.</returns>
    ValueTask OnDeletePreparingAsync(CancellationToken cancellationToken) => default;

    /// <summary>
    /// Called after persisted journal state has been deleted successfully.
    /// </summary>
    void OnDeleteCompleted() { }

    /// <summary>
    /// Called immediately before registered states are captured.
    /// </summary>
    void OnWriteStarted();

    /// <summary>
    /// Called after the write operation completes successfully.
    /// </summary>
    void OnWriteCompleted();

    /// <summary>
    /// Called after all registered states have been restored.
    /// </summary>
    void OnRecoveryCompleted();
}
