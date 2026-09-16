namespace Orleans.Journaling;

/// <summary>
/// Observes durable state manager writes, deletions, initialization replay, and terminal failure.
/// </summary>
/// <remarks>
/// <para>
/// Register observers using <see cref="IJournaledStateManager.RegisterObserver"/> before initialization
/// begins. Each operation uses a stable snapshot of registered observers. Request callbacks run on the
/// caller before queueing, including for requests which share a queued operation. Preparation,
/// finalization, and notifications run on the manager's serialized work loop. The loop awaits one
/// operation at a time, so later queued writes wait for its preparation, finalization, and capture.
/// </para>
/// <para>
/// Every observer prepares before finalization begins, and every observer finalizes before state
/// capture. After finalization completes, start notifications and state capture run synchronously.
/// Completion follows every successful write boundary, including a no-op write which persists zero
/// journal bytes. Initial replay completion follows restoration and recovery callbacks for every
/// registered state, before the manager becomes ready for writes. A fresh manager replays durable
/// state after a failure.
/// </para>
/// <para>
/// A request callback exception rejects that request before admission. An admitted operation's
/// preparation, finalization, capture, or storage failure permanently fences the manager and requests
/// deactivation of its owning grain. Notification exceptions are logged and remaining observers are
/// notified. Preparation and finalization receive the manager's shutdown token; cancellation of a
/// caller's wait leaves queued work running to completion.
/// </para>
/// <para>
/// Pending journal mutations are shared by all callers. Owners stage fallible work in operation-local
/// data and sequence safe application to durable state. Callbacks must complete independently of
/// further state-manager operations, since those operations use the same serialized work loop.
/// </para>
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
    /// Called before registered state is reset and initialization replay begins.
    /// </summary>
    void OnRecoveryStarted() { }

    /// <summary>
    /// Prepares external prerequisites before registered states are captured.
    /// </summary>
    /// <param name="cancellationToken">The state manager shutdown token.</param>
    /// <returns>A task representing the preparation operation.</returns>
    ValueTask OnWritePreparingAsync(CancellationToken cancellationToken) => default;

    /// <summary>
    /// Finalizes prerequisites after every observer has prepared and before state capture begins.
    /// </summary>
    /// <param name="cancellationToken">The state manager shutdown token.</param>
    /// <returns>A task representing the finalization operation.</returns>
    /// <remarks>
    /// Implementations can validate the fully prepared state and mutate only state they exclusively own.
    /// </remarks>
    ValueTask OnWriteFinalizingAsync(CancellationToken cancellationToken) => default;

    /// <summary>
    /// Validates prerequisites before all journaled state is deleted.
    /// </summary>
    /// <param name="cancellationToken">The state manager shutdown token.</param>
    /// <returns>A task representing the validation operation.</returns>
    ValueTask OnDeletePreparingAsync(CancellationToken cancellationToken) => default;

    /// <summary>
    /// Called after persisted journal state has been deleted successfully and registered states have been reset.
    /// </summary>
    void OnDeleteCompleted() { }

    /// <summary>
    /// Called immediately before registered states are captured.
    /// </summary>
    void OnWriteStarted();

    /// <summary>
    /// Called after the write boundary completes successfully, including a no-op write which
    /// persists no journal bytes.
    /// </summary>
    void OnWriteCompleted();

    /// <summary>
    /// Called after initialization replay has restored all registered states.
    /// </summary>
    void OnRecoveryCompleted();

    /// <summary>
    /// Notifies the observer that the manager has been permanently fenced by an operation failure.
    /// </summary>
    /// <param name="exception">The original operation failure.</param>
    /// <remarks>
    /// Called exactly once after the failure is recorded and before admitted and queued operations
    /// are completed with that failure. Use this notification to stop external preparation and callbacks;
    /// durable state must remain unchanged. Exceptions from this callback are logged and isolated.
    /// Caller-wait cancellation completes independently of this notification.
    /// </remarks>
    void OnFaulted(Exception exception) { }
}
