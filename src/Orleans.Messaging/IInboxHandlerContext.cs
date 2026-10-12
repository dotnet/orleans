namespace Orleans.Messaging;

/// <summary>
/// Exposes the received message and its attempt-scoped terminal outcome.
/// </summary>
public interface IInboxHandlerContext
{
    /// <summary>
    /// Gets the received message and its immutable application headers and payload.
    /// </summary>
    /// <remarks>
    /// The context keeps the message available through actual handler completion, including
    /// after Complete or Fail removes the pending message. Keep its payload and headers unchanged while handling the command.
    /// </remarks>
    InboxMessage Message { get; }

    /// <summary>
    /// Synchronously stages inbox completion and transport deduplication for the active attempt.
    /// </summary>
    /// <remarks>
    /// Apply safe-to-commit business mutations and stage outgoing messages before calling Complete
    /// in the same synchronous final block. Return from the handler without further awaits.
    /// The runtime owns the subsequent journal write and acknowledgement.
    /// Repeated completion within the same active attempt coalesces. Completion retains its
    /// logical outcome when cancellation arrives after the final block starts.
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">
    /// The context is retired or belongs to another activation or handler attempt.
    /// </exception>
    void Complete();

    /// <summary>
    /// Synchronously stages a permanent processing failure, retaining the message as a dead letter
    /// and recording its identity for transport deduplication.
    /// </summary>
    /// <param name="reason">The nonblank diagnostic reason for the permanent failure.</param>
    /// <remarks>
    /// Stage any safe-to-commit business changes and outgoing messages before this operation,
    /// then return synchronously. The runtime persists the dead letter and completion together
    /// and owns their journal acknowledgement. The dead letter records the current attempt count.
    /// Repeated calls with the same reason coalesce; Complete preserves an already staged dead letter.
    /// Cancellation after staging preserves the terminal outcome.
    /// Use an application reply and Complete for normal business rejection outcomes.
    /// </remarks>
    /// <exception cref="System.ArgumentException">The reason is blank.</exception>
    /// <exception cref="System.ArgumentNullException">The reason is null.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// The context is retired or belongs to another activation or attempt, or a different
    /// terminal outcome has already been staged.
    /// </exception>
    void Fail(string reason);
}
