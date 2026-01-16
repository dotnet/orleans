namespace Orleans.Journaling.Messaging;

/// <summary>
/// Builder for customizing outgoing envelopes (request context, correlation, etc.).
/// </summary>
public sealed class OutgoingEnvelopeBuilder
{
    /// <summary>
    /// Gets or sets the correlation ID for request/response tracking.
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the reply-to grain ID for callbacks.
    /// </summary>
    public GrainId? ReplyTo { get; set; }

    /// <summary>
    /// Gets or sets the request context dictionary.
    /// </summary>
    public Dictionary<string, object?>? RequestContext { get; set; }

    /// <summary>
    /// Adds or updates a request context value.
    /// </summary>
    /// <param name="key">The context key.</param>
    /// <param name="value">The context value.</param>
    /// <returns>This builder for method chaining.</returns>
    public OutgoingEnvelopeBuilder WithRequestContext(string key, object? value)
    {
        RequestContext ??= new();
        RequestContext[key] = value;
        return this;
    }

    /// <summary>
    /// Sets the correlation ID for request/response tracking.
    /// </summary>
    /// <param name="correlationId">The correlation ID.</param>
    /// <returns>This builder for method chaining.</returns>
    public OutgoingEnvelopeBuilder WithCorrelationId(Guid correlationId)
    {
        CorrelationId = correlationId;
        return this;
    }

    /// <summary>
    /// Sets the reply-to grain for callbacks.
    /// </summary>
    /// <param name="replyTo">The grain ID to send replies to.</param>
    /// <returns>This builder for method chaining.</returns>
    public OutgoingEnvelopeBuilder WithReplyTo(GrainId replyTo)
    {
        ReplyTo = replyTo;
        return this;
    }

    /// <summary>
    /// Clears all builder state.
    /// </summary>
    internal void Reset()
    {
        CorrelationId = null;
        ReplyTo = null;
        RequestContext = null;
    }
}
