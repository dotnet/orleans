using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Handler for messages delivered to a specific route.
/// </summary>
/// <remarks>
/// <para>
/// Handlers are registered with an inbox using <c>IDurableInbox.RegisterHandler(string routeKey, IInboxHandler handler)</c>.
/// For a matching message, the inbox awaits preparation and invokes the returned synchronous action once
/// for that prepared attempt. The action applies the prepared business mutations and outbound messages.
/// </para>
/// <para>
/// For strongly-typed message handling, implement <see cref="IInboxHandler{TMessage}"/> instead, which provides
/// automatic deserialization and type checking.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class PaymentHandler : IInboxHandler&lt;PaymentRequest&gt;
/// {
///     public async ValueTask&lt;Action&gt; PrepareAsync(
///         PaymentRequest request, IInboxHandlerContext context, CancellationToken ct)
///     {
///         var prepared = await PreparePaymentAsync(request, ct);
///         return () =&gt; ApplyPayment(prepared);
///     }
/// }
///
/// inbox.RegisterHandler("payment/process", new PaymentHandler());
/// </code>
/// </example>
public interface IInboxHandler
{
    /// <summary>
    /// Determines whether this handler can handle a message based on its metadata.
    /// </summary>
    /// <param name="context">The handler context containing the envelope and grain information.</param>
    /// <returns><c>true</c> if this handler can process the message; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// This method enables capability-based dispatch, allowing handlers to be selected based on
    /// message metadata (route key, correlation key, context values, etc.) without requiring
    /// pre-registration with explicit route keys.
    /// </para>
    /// <para>
    /// <b>Performance Note:</b> This method should perform fast, metadata-only checks. Avoid
    /// deserialization, I/O operations, or expensive computations. The inbox processing pump
    /// may call this method multiple times per message when searching for a matching handler.
    /// </para>
    /// <para>
    /// Selection is read-only. <see cref="IInboxHandlerContext.CreateEnvelope"/>,
    /// <see cref="IInboxHandlerContext.Send"/>, and <see cref="IInboxHandlerContext.Outbox"/>
    /// throw when called from this method. After selection, <see cref="PrepareAsync"/> prepares local
    /// values and returns the action which stages journaled effects and outgoing messages.
    /// </para>
    /// <para>
    /// <b>Handler Precedence:</b> When multiple handlers return <c>true</c>, the first registered
    /// handler wins. Register more specific handlers before generic ones to ensure correct dispatch.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public class OrderHandler : IInboxHandler&lt;OrderRequest&gt;
    /// {
    ///     public bool CanHandle(IInboxHandlerContext context) =&gt;
    ///         context.Envelope.RouteKey == "order/process";
    ///
    ///     public async ValueTask&lt;Action&gt; PrepareAsync(
    ///         OrderRequest message, IInboxHandlerContext context, CancellationToken ct)
    ///     {
    ///         var prepared = await PrepareOrderAsync(message, ct);
    ///         return () =&gt; ApplyOrder(prepared);
    ///     }
    /// }
    /// </code>
    /// </example>
    bool CanHandle(IInboxHandlerContext context);

    /// <summary>
    /// Prepares a message from the inbox and returns its synchronous apply action.
    /// </summary>
    /// <param name="context">Handler context containing the envelope, grain information, and methods for sending messages.</param>
    /// <param name="cancellationToken">The cancellation token for preparation.</param>
    /// <returns>A task whose result is a non-null synchronous action applying the prepared effects.</returns>
    /// <remarks>
    /// <para>
    /// Perform validation, asynchronous I/O, and other failure-prone work using operation-local values.
    /// Prepare outbound envelopes during this phase. Keep journaled state unchanged until the returned
    /// action is invoked, and call <see cref="IInboxHandlerContext.Send"/> from that action.
    /// </para>
    /// <para>
    /// Messaging awaits preparation and invokes the returned action once for that prepared attempt.
    /// Use a synchronous lambda or method group which applies already-prepared business mutations and
    /// stages prepared messages. An attempt with no effects returns an empty synchronous action.
    /// </para>
    /// <para>
    /// Every applied mutation and outbound message must already be safe to commit. Pending journal
    /// changes are shared by all callers using the grain's state manager, and a journal write captures
    /// those shared changes. Represent expected business failures as validated outcomes during preparation.
    /// </para>
    /// </remarks>
    ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Typed handler adapter for strongly-typed message handling.
/// </summary>
/// <typeparam name="TMessage">The type of message this handler processes.</typeparam>
/// <remarks>
/// <para>
/// Implementing this interface provides automatic deserialization and type checking of the message body.
/// If deserialization fails (type mismatch, missing type, etc.), the handler throws an <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// For handlers that need to handle deserialization failures gracefully, implement <see cref="IInboxHandler"/>
/// directly and use <c>envelope.Data.TryGetBody&lt;T&gt;()</c> to attempt deserialization.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public async ValueTask&lt;Action&gt; PrepareAsync(
///     PaymentRequest message, IInboxHandlerContext context, CancellationToken ct)
/// {
///     var prepared = await PreparePaymentAsync(message, ct);
///     DurableEnvelope? response = null;
///     if (context.Envelope.ReplyTo is { } replyTo)
///     {
///         response = context.CreateEnvelope()
///             .To(replyTo, "payment/response")
///             .WithBody(prepared.Response)
///             .Build();
///     }
///
///     return () =&gt;
///     {
///         ApplyPayment(prepared);
///         if (response is { } envelope)
///         {
///             context.Send(envelope);
///         }
///     };
/// }
/// </code>
/// </example>
public interface IInboxHandler<TMessage> : IInboxHandler
{
    /// <summary>
    /// Prepares a typed message and returns its synchronous apply action.
    /// </summary>
    /// <param name="message">
    /// The deserialized message body. This can be <see langword="null"/> when the sender
    /// serialized a null reference or nullable value.
    /// </param>
    /// <param name="context">Handler context for creating and sending envelopes.</param>
    /// <param name="cancellationToken">The cancellation token for preparation.</param>
    /// <returns>A task whose result is a non-null synchronous action applying the prepared effects.</returns>
    /// <remarks>
    /// Follow the preparation and safe-to-commit staging requirements of
    /// <see cref="IInboxHandler.PrepareAsync"/>.
    /// </remarks>
    ValueTask<Action> PrepareAsync([AllowNull] TMessage message, IInboxHandlerContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Default implementation that returns true (capability check deferred to derived class).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default implementation returns <c>true</c>, meaning typed handlers accept all messages
    /// by default. Derived classes can override <c>CanHandle</c> to add route-based, correlation-based,
    /// or other metadata filters before message processing.
    /// </para>
    /// <para>
    /// Type checking happens during preparation when the message body is deserialized.
    /// This design allows handlers to inspect metadata without deserialization overhead.
    /// </para>
    /// </remarks>
    bool IInboxHandler.CanHandle(IInboxHandlerContext context) => true;

    /// <summary>
    /// Default implementation with type check and deferred deserialization.
    /// </summary>
    /// <remarks>
    /// This method attempts to deserialize the envelope body as <typeparamref name="TMessage"/>.
    /// If deserialization fails, it throws an <see cref="InvalidOperationException"/>.
    /// </remarks>
    ValueTask<Action> IInboxHandler.PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (context.Envelope.Data.TryGetBody<TMessage>(out var typed))
        {
            return PrepareAsync(typed, context, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Failed to deserialize message body for route '{context.Envelope.RouteKey}' as '{typeof(TMessage).FullName}'.");
    }
}
