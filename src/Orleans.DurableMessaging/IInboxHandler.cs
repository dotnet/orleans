using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Processes opaque messages accepted by a grain's durable inbox.
/// </summary>
/// <remarks>
/// Register one handler per inbox. Applications perform payload decoding and dispatch in their handler
/// and stage outbound messages through an injected <see cref="IDurableOutbox"/>.
/// </remarks>
public interface IInboxHandler
{
    /// <summary>
    /// Handles a message and stages its logical completion.
    /// </summary>
    /// <param name="context">The received envelope and attempt-scoped completion operation.</param>
    /// <param name="cancellationToken">The token to check during preparation and before shared mutation.</param>
    /// <returns>The handler's method outcome, awaited by the inbox runtime.</returns>
    /// <remarks>
    /// <para>
    /// Decode the payload, perform asynchronous I/O, validate local results, build outgoing envelopes,
    /// and check cancellation before the first shared business or journaled mutation.
    /// </para>
    /// <para>
    /// From the first shared mutation through completion of this method, execute synchronously.
    /// Apply complete, safe-to-commit business changes, stage outgoing messages through
    /// <see cref="IDurableOutbox.Send"/>, call <see cref="IInboxHandlerContext.Complete"/>, and return.
    /// This final block includes the method's return after Complete and relies on the trusted
    /// handler contract. Every successful outcome calls Complete, including outcomes with no business effects.
    /// </para>
    /// <para>
    /// Complete stages inbox completion and transport deduplication alongside the business changes
    /// and outgoing intents. The runtime owns the subsequent journal write, persistence acknowledgement,
    /// and retirement. Failures during local preparation follow the inbox retry and dead-letter policy.
    /// An error after Complete preserves the completed logical outcome and is reported by the runtime.
    /// </para>
    /// </remarks>
    ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}
