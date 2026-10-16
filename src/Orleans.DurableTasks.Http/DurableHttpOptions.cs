using Microsoft.AspNetCore.Http;

namespace Orleans.DurableTasks.Http;

/// <summary>Configures the HTTP operations which expose durable tasks.</summary>
public sealed class DurableHttpOptions
{
    /// <summary>
    /// Gets or sets whether a request which starts a durable task must carry an <c>Idempotency-Key</c> header.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, the default, a request without a key starts a new task under a generated identifier,
    /// so each such request starts its own operation. When <see langword="true"/>, the request is rejected with
    /// <c>400 Bad Request</c>.
    /// </remarks>
    public bool RequireIdempotencyKey { get; set; }

    /// <summary>
    /// Gets or sets the function which maps an operation identifier, which the client chooses with its
    /// <c>Idempotency-Key</c> and which addresses the operation in URLs, to the identifier which the operation's key
    /// carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operation's key is its group's scope or path, the request's values for the group's route parameters, and the
    /// mapped identifier. The key names the operation's host grain and the root task which performs its work. The
    /// endpoints apply the function to every request which names an operation, both the request which starts it and the
    /// requests which observe, cancel, or signal it, so it scopes operations: equal identifiers which map to distinct
    /// identifiers address distinct operations. The default returns the operation identifier unchanged.
    /// </para>
    /// <para>
    /// An application scopes identifiers by what is relevant to its business, such as an account or a client application,
    /// for example <c>(context, id) =&gt; $"{GetAccountId(context.User)}:{id}"</c>, as Stripe scopes idempotency keys to
    /// accounts. It can also hash the identifier.
    /// </para>
    /// </remarks>
    public Func<HttpContext, string, string> MapOperationId { get; set; } = static (_, operationId) => operationId;

    /// <summary>
    /// Gets or sets the interval which responses for running operations advertise in their <c>Retry-After</c> header, as
    /// the minimum time before a client polls the operation again.
    /// </summary>
    /// <remarks>The header carries whole seconds, so the interval is rounded up. The default is one second.</remarks>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the longest wait which a request's <c>Prefer: wait</c> preference receives before the response reports
    /// a running operation.
    /// </summary>
    /// <remarks>The default is one minute.</remarks>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the function which describes a failed or canceled task's exception to clients, in the operation's
    /// <see cref="DurableOperation.Error"/>.
    /// </summary>
    /// <remarks>
    /// Exception messages can describe internal state, such as hosts and grain identities, so the default describes only
    /// the outcome: <c>Canceled</c> for an <see cref="OperationCanceledException"/>, and <c>Failed</c> otherwise. An
    /// application which defines exceptions for its clients returns their details, for example:
    /// <code>
    /// options.CreateError = exception => exception is OrderRejectedException rejected
    ///     ? new("OrderRejected", rejected.Message)
    ///     : DurableHttpOptions.DefaultCreateError(exception);
    /// </code>
    /// </remarks>
    public Func<Exception, DurableOperationError> CreateError { get; set; } = DefaultCreateError;

    /// <summary>Describes an exception by the task's outcome alone, without its type or message.</summary>
    /// <param name="exception">The exception which failed or canceled the task.</param>
    /// <returns>An error whose code is <c>Canceled</c> or <c>Failed</c>.</returns>
    public static DurableOperationError DefaultCreateError(Exception exception) => exception is OperationCanceledException
        ? new("Canceled", "The operation was canceled.")
        : new("Failed", "The operation failed.");
}
