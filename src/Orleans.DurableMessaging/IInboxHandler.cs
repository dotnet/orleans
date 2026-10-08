using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Handles messages selected by an inbox.
/// </summary>
/// <remarks>
/// The inbox awaits <see cref="HandleAsync"/> and owns the subsequent journal write and resource cleanup.
/// Handlers stage their logical completion using <see cref="IInboxHandlerContext.Complete"/>.
/// Implement <see cref="IInboxHandler{TMessage}"/> for automatic body deserialization and type checking.
/// </remarks>
/// <example>
/// <code>
/// public class PaymentHandler : IInboxHandler&lt;PaymentRequest&gt;
/// {
///     public async ValueTask HandleAsync(
///         PaymentRequest request, IInboxHandlerContext context, CancellationToken ct)
///     {
///         var prepared = await PreparePaymentAsync(request, ct);
///         ValidatePayment(prepared);
///         ct.ThrowIfCancellationRequested();
///         ApplyPayment(prepared);
///         context.Complete();
///     }
/// }
/// </code>
/// </example>
public interface IInboxHandler
{
    /// <summary>
    /// Determines whether this handler can handle a message based on its metadata.
    /// </summary>
    /// <param name="context">The context containing envelope metadata and grain identity.</param>
    /// <returns><c>true</c> if this handler can process the message; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Use a pure, fast metadata predicate. The handler owns the purity of its business and injected-state access.
    /// Selection exposes read-only envelope metadata and identity; outbox access, envelope creation, sending,
    /// and <see cref="IInboxHandlerContext.Complete"/> are rejected by the selection context.
    /// When multiple handlers match, the first registered handler wins. Register more specific handlers first.
    /// </remarks>
    bool CanHandle(IInboxHandlerContext context);

    /// <summary>
    /// Handles the message and synchronously stages its logical completion.
    /// </summary>
    /// <param name="context">The context for envelope inspection, outgoing messages, and completion.</param>
    /// <param name="cancellationToken">The cancellation token to check before the first shared mutation.</param>
    /// <returns>A completion representing the handler's method outcome.</returns>
    /// <remarks>
    /// <para>
    /// Complete asynchronous I/O, validation, cancellation checks, and other fallible work using local values
    /// before the first shared business or journaled mutation. Build outgoing envelopes locally. Optional
    /// <see cref="IDurableOutbox.PrepareSendAsync"/> acquires a wakeup prerequisite earlier; consume and handle
    /// its result before starting shared updates.
    /// </para>
    /// <para>
    /// From the first shared mutation through completion of this method, execute synchronously without awaits,
    /// including after calling <see cref="IInboxHandlerContext.Complete"/>. Apply complete, safe-to-commit business
    /// changes, stage outgoing envelopes or prepared batches, then call <see cref="IInboxHandlerContext.Complete"/>
    /// and return. These changes share the activation turn with inbox completion and deduplication.
    /// An outcome with no business effects still calls Complete. This mutation boundary is a trusted handler contract.
    /// </para>
    /// <para>
    /// Complete stages feature state synchronously. The runtime awaits this method's outcome, then owns the
    /// actual journal write, acknowledgement, and attempt resource cleanup. Prepared batches remain owned
    /// through the actual persistence outcome. Expected business failures are validated outcomes during local
    /// preparation. Shared pending mutations must already be safe to commit with other callers' changes.
    /// </para>
    /// </remarks>
    ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Provides automatic body deserialization and type checking for a typed handler.
/// </summary>
/// <typeparam name="TMessage">The message body type.</typeparam>
/// <remarks>
/// The default adapter inspects the declared body type, deserializes it, and forwards the context and token
/// to the typed handler. It propagates the handler outcome directly. The handler calls
/// <see cref="IInboxHandlerContext.Complete"/> according to <see cref="IInboxHandler.HandleAsync"/>.
/// </remarks>
/// <example>
/// <code>
/// public async ValueTask HandleAsync(
///     PaymentRequest request, IInboxHandlerContext context, CancellationToken ct)
/// {
///     var prepared = await PreparePaymentAsync(request, ct);
///     DurableEnvelope? response = null;
///     if (context.Envelope.ReplyTo is { } replyTo)
///     {
///         response = context.CreateEnvelope()
///             .To(replyTo, "payment/response")
///             .WithBody(prepared.Response)
///             .Build();
///     }
///     ValidatePayment(prepared);
///     ct.ThrowIfCancellationRequested();
///     ApplyPayment(prepared);
///     if (response is { } envelope)
///     {
///         context.Send(envelope);
///     }
///     context.Complete();
/// }
/// </code>
/// </example>
public interface IInboxHandler<TMessage> : IInboxHandler
{
    /// <summary>
    /// Handles a typed message and stages logical completion.
    /// </summary>
    /// <param name="message">The deserialized body, which can be null for a null reference or nullable value.</param>
    /// <param name="context">The context for envelope inspection, outgoing messages, and completion.</param>
    /// <param name="cancellationToken">The token to check before the first shared mutation.</param>
    /// <returns>A completion representing the handler's method outcome.</returns>
    /// <remarks>Follow the local preparation and synchronous final-block contract of <see cref="IInboxHandler.HandleAsync"/>.</remarks>
    ValueTask HandleAsync([AllowNull] TMessage message, IInboxHandlerContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Accepts all metadata by default, before body deserialization.
    /// </summary>
    /// <remarks>Override the metadata predicate to restrict route or correlation selection.</remarks>
    bool IInboxHandler.CanHandle(IInboxHandlerContext context) => true;

    /// <summary>
    /// Deserializes the body and directly invokes the typed handler.
    /// </summary>
    /// <exception cref="InvalidOperationException">The body cannot be deserialized as <typeparamref name="TMessage"/>.</exception>
    ValueTask IInboxHandler.HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (context.Envelope.Data.TryGetBody<TMessage>(out var typed))
        {
            return HandleAsync(typed, context, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Failed to deserialize message body for route '{context.Envelope.RouteKey}' as '{typeof(TMessage).FullName}'.");
    }
}
