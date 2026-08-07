using System;

namespace Orleans.DurableMessaging;

/// <summary>
/// Standard error response message for durable inbox/outbox error handling.
/// </summary>
/// <remarks>
/// <para>
/// DurableErrorResponse provides a standardized message format for communicating errors
/// back to requesters in durable RPC (request/response) scenarios. When a handler encounters
/// an error while processing a message with a <see cref="DurableEnvelope.ReplyTo"/> set,
/// it should send a DurableErrorResponse to the ReplyTo address to notify the requester
/// of the failure.
/// </para>
/// <para>
/// The <see cref="ErrorCode"/> property enables categorization of errors for automated
/// error handling logic. Use standard error codes (see <see cref="StandardErrorCodes"/>)
/// or define custom error codes as needed. The <see cref="IsRetriable"/> property indicates
/// whether the requester should retry the operation.
/// </para>
/// <para>
/// For debugging and diagnostics, the optional <see cref="ExceptionDetails"/> property
/// can include exception type, message, and stack trace information. In production environments,
/// consider omitting or redacting sensitive exception details to avoid information disclosure.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Handler encounters an error and sends error response
/// public class OrderHandler : IInboxHandler&lt;OrderRequest&gt;
/// {
///     public async ValueTask HandleAsync(OrderRequest message, IInboxHandlerContext context, CancellationToken ct)
///     {
///         try
///         {
///             await ProcessOrder(message, ct);
///         }
///         catch (InsufficientInventoryException ex)
///         {
///             // Send error response to requester
///             if (context.Envelope.ReplyTo is { } replyTo)
///             {
///                 var errorResponse = new DurableErrorResponse
///                 {
///                     ErrorCode = "INSUFFICIENT_INVENTORY",
///                     Message = $"Cannot fulfill order: {ex.Message}",
///                     ExceptionDetails = ex.ToString(),
///                     IsRetriable = false  // Business rule violation, no retry
///                 };
///                 
///                 var envelope = context.CreateEnvelope()
///                     .To(replyTo, "order/reply")
///                     .WithBody(errorResponse)
///                     .WithCorrelationKey(context.Envelope.CorrelationKey)
///                     .Build();
///                 
///                 context.Send(envelope);
///             }
///             throw;  // Rethrow to mark processing as failed
///         }
///         catch (DatabaseConnectionException ex)
///         {
///             // Transient error - indicate it's retriable
///             if (context.Envelope.ReplyTo is { } replyTo)
///             {
///                 var errorResponse = new DurableErrorResponse
///                 {
///                     ErrorCode = StandardErrorCodes.TransientError,
///                     Message = "Temporary database connectivity issue",
///                     ExceptionDetails = ex.ToString(),
///                     IsRetriable = true  // Transient error, safe to retry
///                 };
///                 
///                 var envelope = context.CreateEnvelope()
///                     .To(replyTo, "order/reply")
///                     .WithBody(errorResponse)
///                     .WithCorrelationKey(context.Envelope.CorrelationKey)
///                     .Build();
///                 
///                 context.Send(envelope);
///             }
///             throw;
///         }
///     }
/// }
/// </code>
/// </example>
[GenerateSerializer, Immutable]
public readonly struct DurableErrorResponse
{
    /// <summary>
    /// Error code for categorization and automated error handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use standard error codes from <see cref="StandardErrorCodes"/> for common error scenarios,
    /// or define custom error codes specific to your domain. Error codes enable requesters to
    /// implement automated error handling logic without parsing error messages.
    /// </para>
    /// <para>
    /// Error codes should be stable identifiers that don't change between versions. Use
    /// human-readable constants (e.g., "HANDLER_NOT_FOUND", "DESERIALIZATION_FAILED") rather
    /// than numeric codes for clarity.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Using standard error codes
    /// var error = new DurableErrorResponse
    /// {
    ///     ErrorCode = StandardErrorCodes.HandlerNotFound,
    ///     Message = "No handler registered for route 'payment/process'"
    /// };
    /// 
    /// // Using custom error codes
    /// var error = new DurableErrorResponse
    /// {
    ///     ErrorCode = "PAYMENT_DECLINED",
    ///     Message = "Payment authorization failed: insufficient funds"
    /// };
    /// </code>
    /// </example>
    [Id(0)]
    public required string ErrorCode { get; init; }

    /// <summary>
    /// Human-readable error message describing what went wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The message should provide enough context for developers and operators to understand
    /// the error, but should avoid exposing sensitive information like passwords, API keys,
    /// or personally identifiable information (PII).
    /// </para>
    /// <para>
    /// For detailed technical information including stack traces, use the optional
    /// <see cref="ExceptionDetails"/> property instead. This separation allows production
    /// deployments to omit detailed exception information while keeping user-facing messages.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Good: Descriptive without sensitive details
    /// Message = "Failed to process payment: card declined by issuer"
    /// 
    /// // Bad: Exposes internal details
    /// Message = "SQL query failed: SELECT * FROM payments WHERE card_number='4111-1111-1111-1111'"
    /// </code>
    /// </example>
    [Id(1)]
    public required string Message { get; init; }

    /// <summary>
    /// Optional detailed exception information for debugging and diagnostics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property can include exception type, message, stack trace, inner exceptions,
    /// and any other diagnostic information that would be useful during debugging. Unlike
    /// <see cref="Message"/>, this property is intended for technical audiences and may
    /// include implementation details.
    /// </para>
    /// <para>
    /// In production environments, consider omitting or redacting this property to avoid
    /// information disclosure. You can control whether to include exception details based
    /// on configuration settings or environment variables.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Development: Full exception details
    /// ExceptionDetails = exception.ToString()
    /// 
    /// // Production: Omit or redact sensitive details
    /// ExceptionDetails = Environment.IsDevelopment() ? exception.ToString() : null
    /// 
    /// // Structured exception details
    /// ExceptionDetails = $"Type: {exception.GetType().FullName}\nMessage: {exception.Message}"
    /// </code>
    /// </example>
    [Id(2)]
    public string? ExceptionDetails { get; init; }

    /// <summary>
    /// Indicates whether the requester should retry the operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set to <c>true</c> for transient errors that may succeed on retry (e.g., temporary network
    /// failures, database connection timeouts, rate limiting). Set to <c>false</c> for permanent
    /// errors that will not succeed on retry (e.g., validation failures, business rule violations,
    /// authorization errors).
    /// </para>
    /// <para>
    /// This flag enables requesters to implement automated retry logic with backoff strategies
    /// for transient failures while immediately failing fast for permanent errors.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Transient error - safe to retry
    /// IsRetriable = true   // Network timeout, database connection failure, rate limit exceeded
    /// 
    /// // Permanent error - do not retry
    /// IsRetriable = false  // Validation error, authorization failure, resource not found
    /// </code>
    /// </example>
    [Id(3)]
    public bool IsRetriable { get; init; }
}

/// <summary>
/// Standard error codes for common failure scenarios in durable inbox/outbox messaging.
/// </summary>
/// <remarks>
/// <para>
/// These constants provide a shared vocabulary for common error scenarios across Orleans.Journaling.
/// Using standard error codes enables consistent error handling patterns and automated retry logic.
/// </para>
/// <para>
/// Applications can define additional domain-specific error codes as needed. Error codes should be
/// stable identifiers that don't change between versions.
/// </para>
/// </remarks>
public static class StandardErrorCodes
{
    /// <summary>
    /// No handler is registered for the specified route key.
    /// </summary>
    /// <remarks>
    /// Indicates that the target grain does not have a handler registered for the message's route key.
    /// This is typically a configuration error or indicates the message was sent to the wrong grain.
    /// Not retriable - the sender should verify the target grain and route key.
    /// </remarks>
    public const string HandlerNotFound = "HANDLER_NOT_FOUND";

    /// <summary>
    /// Failed to deserialize the message body.
    /// </summary>
    /// <remarks>
    /// Indicates that the message body could not be deserialized into the expected type.
    /// This may occur due to schema evolution, serialization compatibility issues, or corrupted data.
    /// Not retriable - the sender may need to update the message format or schema.
    /// </remarks>
    public const string DeserializationFailed = "DESERIALIZATION_FAILED";

    /// <summary>
    /// Unhandled exception occurred in the message handler.
    /// </summary>
    /// <remarks>
    /// Indicates that the handler threw an unexpected exception while processing the message.
    /// Check the ExceptionDetails property for diagnostic information.
    /// Retriability depends on the specific exception type.
    /// </remarks>
    public const string HandlerException = "HANDLER_EXCEPTION";

    /// <summary>
    /// The operation was cancelled.
    /// </summary>
    /// <remarks>
    /// Indicates that message processing was cancelled, typically due to grain deactivation,
    /// silo shutdown, or explicit cancellation via CancellationToken.
    /// May be retriable depending on the cancellation reason.
    /// </remarks>
    public const string Cancelled = "CANCELLED";

    /// <summary>
    /// The operation timed out.
    /// </summary>
    /// <remarks>
    /// Indicates that message processing exceeded the configured timeout threshold.
    /// This may indicate a slow operation, resource contention, or a deadlock.
    /// Often retriable, but may require investigation of the underlying cause.
    /// </remarks>
    public const string Timeout = "TIMEOUT";

    /// <summary>
    /// A transient error occurred that may succeed on retry.
    /// </summary>
    /// <remarks>
    /// General error code for transient failures such as temporary network issues,
    /// database connection failures, or service unavailability.
    /// Retriable with exponential backoff.
    /// </remarks>
    public const string TransientError = "TRANSIENT_ERROR";

    /// <summary>
    /// Validation of the message content failed.
    /// </summary>
    /// <remarks>
    /// Indicates that the message body failed validation (e.g., missing required fields,
    /// invalid data format, business rule violation).
    /// Not retriable - the sender must correct the message content.
    /// </remarks>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>
    /// The operation is not authorized.
    /// </summary>
    /// <remarks>
    /// Indicates that the sender does not have permission to perform the requested operation.
    /// Not retriable - the sender must obtain appropriate authorization.
    /// </remarks>
    public const string Unauthorized = "UNAUTHORIZED";
}
