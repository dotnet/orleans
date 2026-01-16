namespace Orleans.Journaling.Messaging;

/// <summary>
/// Handler for messages delivered to a specific route.
/// </summary>
public interface IInboxHandler
{
    /// <summary>
    /// Handles a message from the inbox.
    /// </summary>
    /// <param name="envelope">The full message envelope (exposed for metadata access).</param>
    /// <param name="context">Handler context for sending replies and accessing outbox.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Typed handler adapter for strongly-typed message handling.
/// </summary>
/// <typeparam name="TMessage">The type of message this handler processes.</typeparam>
public interface IInboxHandler<TMessage> : IInboxHandler
{
    /// <summary>
    /// Handles a typed message.
    /// </summary>
    /// <param name="message">The deserialized message body.</param>
    /// <param name="context">Handler context for sending replies and accessing outbox.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask HandleAsync(TMessage message, IInboxHandlerContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Default implementation with type check and deferred deserialization.
    /// </summary>
    ValueTask IInboxHandler.HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (envelope.Data.TryGetBody<TMessage>(out var typed))
        {
            return HandleAsync(typed, context, cancellationToken);
        }

        throw new InvalidOperationException($"Failed to deserialize message body as {typeof(TMessage).Name}");
    }
}
