namespace Orleans.Journaling.Messaging;

/// <summary>
/// Context available during inbox message handling.
/// Exposes the envelope directly and provides Send customization.
/// </summary>
public interface IInboxHandlerContext
{
    /// <summary>
    /// The envelope being processed.
    /// </summary>
    DurableEnvelope Envelope { get; }

    /// <summary>
    /// Gets the current grain's grain ID.
    /// </summary>
    GrainId GrainId { get; }

    /// <summary>
    /// Sends a reply to the ReplyTo grain (if present in envelope).
    /// </summary>
    /// <typeparam name="TBody">The response body type.</typeparam>
    /// <param name="responseBody">The response body.</param>
    /// <param name="configureEnvelope">Optional action to customize the outgoing envelope (set headers, etc.).</param>
    void Reply<TBody>(TBody responseBody, Action<OutgoingEnvelopeBuilder>? configureEnvelope = null);

    /// <summary>
    /// Sends a message via the outbox.
    /// </summary>
    /// <typeparam name="TBody">The message body type.</typeparam>
    /// <param name="target">Target grain ID.</param>
    /// <param name="routeKey">Route key for handler dispatch on target.</param>
    /// <param name="body">Message body.</param>
    /// <param name="configureEnvelope">Optional action to customize the outgoing envelope.</param>
    void Send<TBody>(GrainId target, string routeKey, TBody body, Action<OutgoingEnvelopeBuilder>? configureEnvelope = null);

    /// <summary>
    /// Gets the current grain's outbox for advanced scenarios.
    /// </summary>
    IDurableOutbox Outbox { get; }
}
