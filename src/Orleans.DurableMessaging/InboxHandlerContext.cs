using Orleans.Runtime;
using Orleans.Serialization.Session;

namespace Orleans.DurableMessaging;

/// <summary>
/// Implementation of <see cref="IInboxHandlerContext"/> that provides handler access to envelope metadata
/// and methods for creating and sending outbound messages.
/// </summary>
/// <remarks>
/// <para>
/// This class is instantiated by the inbox processing pump when invoking handlers. It wraps the current
/// envelope and outbox, and provides a factory method for creating pre-configured envelope builders.
/// </para>
/// <para>
/// The implementation is immutable and thread-safe. Envelope builders created via <see cref="CreateEnvelope"/>
/// are independent instances and can be used concurrently (though individual builders are not thread-safe).
/// </para>
/// </remarks>
internal sealed class InboxHandlerContext : IInboxHandlerContext
{
    private readonly SerializerSessionPool _sessionPool;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxHandlerContext"/> class.
    /// </summary>
    /// <param name="envelope">The envelope being processed.</param>
    /// <param name="grainId">The current grain's identity.</param>
    /// <param name="outbox">The outbox for sending messages.</param>
    /// <param name="sessionPool">The serializer session pool for creating envelope builders.</param>
    /// <remarks>
    /// This constructor is typically called by the inbox processing pump. The parameters are captured
    /// and exposed via the <see cref="IInboxHandlerContext"/> interface properties.
    /// </remarks>
    public InboxHandlerContext(
        DurableEnvelope envelope,
        GrainId grainId,
        IDurableOutbox outbox,
        SerializerSessionPool sessionPool)
    {
        Envelope = envelope;
        GrainId = grainId;
        Outbox = outbox;
        _sessionPool = sessionPool;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The envelope contains all message metadata including <c>SenderId</c>, <c>CorrelationKey</c>,
    /// <c>ReplyTo</c>, and <c>CreatedAt</c>. The <c>Data</c> property provides access to the body
    /// and request context values via deferred deserialization.
    /// </remarks>
    public DurableEnvelope Envelope { get; }

    /// <inheritdoc />
    /// <remarks>
    /// This GrainId is automatically set as the <c>SenderId</c> on all envelopes created via
    /// <see cref="CreateEnvelope"/>.
    /// </remarks>
    public GrainId GrainId { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Direct access to the outbox is provided for advanced scenarios. Most handlers should use
    /// <see cref="Send"/> instead of calling <c>Outbox.Send()</c> directly.
    /// </remarks>
    public IDurableOutbox Outbox { get; }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The returned builder has its internal properties pre-configured:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>SessionPool</c> - Set to the grain's serializer session pool</description></item>
    /// <item><description><c>SenderId</c> - Set to the current grain's GrainId</description></item>
    /// </list>
    /// <para>
    /// Each call creates a new builder instance. Builders are lightweight and intended to be used
    /// for a single envelope creation, then discarded. For high-throughput scenarios, the builder
    /// supports pooling via its internal <c>Reset()</c> method, though this is typically managed
    /// by infrastructure code rather than user handlers.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Create and send a message
    /// var envelope = context.CreateEnvelope()
    ///     .To(targetGrain, "order/confirm")
    ///     .WithBody(new OrderConfirmation { OrderId = orderId })
    ///     .Build();
    ///
    /// context.Send(envelope);
    ///
    /// // Create multiple messages with the same context
    /// var notification = context.CreateEnvelope()
    ///     .To(notificationGrain, "notification/send")
    ///     .WithBody(new Notification { Message = "Order confirmed" })
    ///     .Build();
    ///
    /// var audit = context.CreateEnvelope()
    ///     .To(auditGrain, "audit/log")
    ///     .WithBody(new AuditEvent { Action = "OrderConfirmed", OrderId = orderId })
    ///     .Build();
    ///
    /// context.Send(notification);
    /// context.Send(audit);
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder CreateEnvelope()
    {
        return new DurableEnvelopeBuilder
        {
            SessionPool = _sessionPool,
            SenderId = GrainId
        }.WithCurrentRequestContext();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The envelope is added to the outbox immediately. Persistence is owned by the inbox handler context:
    /// direct state writes made by the handler are deferred, then handler state, outbound messages, inbox
    /// removal, and deduplication completion are committed together after successful return.
    /// </para>
    /// <para>
    /// If the handler throws an exception before state is persisted, the message will not be sent.
    /// This provides transactional semantics: either the handler completes and all outbound messages
    /// are sent, or the handler fails and no messages are sent.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public async ValueTask HandleAsync(OrderRequest request, IInboxHandlerContext context, CancellationToken ct)
    /// {
    ///     // Process order (may throw exceptions)
    ///     var result = await ProcessOrder(request);
    ///
    ///     // These messages are only persisted if ProcessOrder succeeds
    ///     var confirmation = context.CreateEnvelope()
    ///         .To(request.CustomerId, "order/confirmed")
    ///         .WithBody(result)
    ///         .Build();
    ///     context.Send(confirmation);
    ///
    ///     var fulfillment = context.CreateEnvelope()
    ///         .To(fulfillmentGrain, "fulfillment/create")
    ///         .WithBody(result)
    ///         .Build();
    ///     context.Send(fulfillment);
    ///
    ///     // If we reach here, both messages will be persisted atomically
    /// }
    /// </code>
    /// </example>
    public void Send(DurableEnvelope envelope)
    {
        Outbox.Send(envelope);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Creates a <see cref="DurableErrorResponse"/> with the specified error code and message,
    /// and sends it to the <see cref="DurableEnvelope.ReplyTo"/> address if present. The error
    /// response includes the error code, message, and retriability flag.
    /// </para>
    /// <para>
    /// The reply route key is derived from the original request's route key. For example:
    /// </para>
    /// <list type="bullet">
    /// <item><description>"order/process" becomes "order/reply"</description></item>
    /// <item><description>"payment/authorize" becomes "payment/reply"</description></item>
    /// <item><description>"notify" (no prefix) becomes "reply"</description></item>
    /// </list>
    /// <para>
    /// If the envelope does not have a ReplyTo address, this method does nothing (safe no-op
    /// for one-way messages).
    /// </para>
    /// </remarks>
    public void SendError(string errorCode, string message, bool isRetriable = false)
    {
        // Only send error if there's a reply-to address
        if (Envelope.ReplyTo is not { } replyTo)
        {
            return;
        }

        // Create error response
        var errorResponse = new DurableErrorResponse
        {
            ErrorCode = errorCode,
            Message = message,
            IsRetriable = isRetriable
        };

        // Determine reply route key
        // If route key has a prefix (e.g., "order/process"), use "prefix/reply"
        // Otherwise, just use "reply"
        var replyRoute = GetReplyRouteKey(Envelope.RouteKey);

        // Build and send error response envelope
        var builder = CreateEnvelope()
            .To(replyTo, replyRoute)
            .WithBody(errorResponse);

        // Preserve correlation key if present
        if (Envelope.CorrelationKey is { } correlationKey)
        {
            builder.WithCorrelationKey(correlationKey);
        }

        var errorEnvelope = builder.Build();
        Send(errorEnvelope);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Extracts error details from the exception and creates a <see cref="DurableErrorResponse"/>.
    /// The exception type name is converted to an error code (e.g., "ArgumentNullException" becomes
    /// "ARGUMENT_NULL_EXCEPTION"), and the full exception details (including stack trace) are
    /// included in the <see cref="DurableErrorResponse.ExceptionDetails"/> property.
    /// </para>
    /// <para>
    /// If the envelope does not have a ReplyTo address, this method does nothing (safe no-op
    /// for one-way messages).
    /// </para>
    /// </remarks>
    public void SendError(Exception exception, bool isRetriable = false)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Only send error if there's a reply-to address
        if (Envelope.ReplyTo is not { } replyTo)
        {
            return;
        }

        // Convert exception type name to error code (e.g., ArgumentNullException -> ARGUMENT_NULL_EXCEPTION)
        var exceptionTypeName = exception.GetType().Name;
        var errorCode = ConvertToErrorCode(exceptionTypeName);

        // Create error response with full exception details
        var errorResponse = new DurableErrorResponse
        {
            ErrorCode = errorCode,
            Message = exception.Message,
            ExceptionDetails = exception.ToString(),
            IsRetriable = isRetriable
        };

        // Determine reply route key
        var replyRoute = GetReplyRouteKey(Envelope.RouteKey);

        // Build and send error response envelope
        var builder = CreateEnvelope()
            .To(replyTo, replyRoute)
            .WithBody(errorResponse);

        // Preserve correlation key if present
        if (Envelope.CorrelationKey is { } correlationKey)
        {
            builder.WithCorrelationKey(correlationKey);
        }

        var errorEnvelope = builder.Build();
        Send(errorEnvelope);
    }

    /// <summary>
    /// Determines the reply route key based on the original request's route key.
    /// </summary>
    /// <param name="requestRouteKey">The route key from the original request.</param>
    /// <returns>The reply route key (e.g., "order/process" becomes "order/reply").</returns>
    private static string GetReplyRouteKey(string? requestRouteKey)
    {
        if (string.IsNullOrEmpty(requestRouteKey))
        {
            return "reply";
        }

        // If route has a prefix (contains '/'), replace suffix with "reply"
        var lastSlashIndex = requestRouteKey.LastIndexOf('/');
        if (lastSlashIndex >= 0)
        {
            return requestRouteKey.Substring(0, lastSlashIndex + 1) + "reply";
        }

        // No prefix, just use "reply"
        return "reply";
    }

    /// <summary>
    /// Converts an exception type name to an error code.
    /// </summary>
    /// <param name="exceptionTypeName">The exception type name (e.g., "ArgumentNullException").</param>
    /// <returns>The error code in uppercase with underscores (e.g., "ARGUMENT_NULL_EXCEPTION").</returns>
    private static string ConvertToErrorCode(string exceptionTypeName)
    {
        // Remove "Exception" suffix if present
        if (exceptionTypeName.EndsWith("Exception", StringComparison.Ordinal))
        {
            exceptionTypeName = exceptionTypeName.Substring(0, exceptionTypeName.Length - "Exception".Length);
        }

        // Convert PascalCase to UPPER_SNAKE_CASE
        var result = System.Text.RegularExpressions.Regex.Replace(
            exceptionTypeName,
            "([a-z])([A-Z])",
            "$1_$2");

        return result.ToUpperInvariant();
    }
}
