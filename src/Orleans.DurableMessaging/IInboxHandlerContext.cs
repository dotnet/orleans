namespace Orleans.DurableMessaging;

/// <summary>
/// Exposes the received message and its attempt-scoped logical completion.
/// </summary>
public interface IInboxHandlerContext
{
    /// <summary>
    /// Gets the received transport envelope and immutable application bytes.
    /// </summary>
    DurableEnvelope Envelope { get; }

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
}
