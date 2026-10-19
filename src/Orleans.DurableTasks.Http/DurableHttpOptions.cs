using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Orleans.DurableTasks.Http;

/// <summary>Configures the HTTP operations which expose durable tasks.</summary>
public sealed class DurableHttpOptions
{
    /// <summary>
    /// Gets or sets whether a request which starts a durable task must carry an <c>Idempotency-Key</c> header.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the default, a request without a key is rejected with <c>400 Bad Request</c>, so every
    /// operation has a key which its client retries with. When <see langword="false"/>, a request without a key starts a
    /// new operation under a generated identifier, which a retry of the request can't reach.
    /// </remarks>
    public bool RequireIdempotencyKey { get; set; } = true;

    /// <summary>
    /// Gets or sets the function which returns the authorization scope of a request's caller, such as the user or the
    /// account which it authenticates as, or <see langword="null"/> for an anonymous caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operation's key is its group's scope, the request's values for the group's route parameters, the caller's scope,
    /// and the operation identifier, each a segment of its own. Every request which names an operation, both the request
    /// which starts it and the requests which observe, cancel, signal, or attach callbacks to it, applies the function, so
    /// a caller reaches only the operations of its own scope: an operation identifier names an operation of the caller
    /// which uses it, not a capability which any caller who learns it holds. Anonymous callers share one scope.
    /// </para>
    /// <para>
    /// The default, <see cref="DefaultGetCallerScope"/>, returns the authenticated user's
    /// <see cref="ClaimTypes.NameIdentifier"/> or <c>sub</c> claim. An application scopes operations by what its
    /// authorization grants, such as an account which several users share:
    /// <c>options.GetCallerScope = context => context.User.FindFirst("account")?.Value;</c>. An authenticated caller
    /// whose scope is <see langword="null"/> is rejected, so authenticated callers never share the anonymous scope.
    /// </para>
    /// </remarks>
    public Func<HttpContext, string?> GetCallerScope { get; set; } = DefaultGetCallerScope;

    /// <summary>
    /// Gets or sets the function which decides whether the server accepts a callback URL which a request registers, such
    /// as with an <c>Operation-Callback</c> header or a Nexus <c>callback</c>, or <see langword="null"/>, the default,
    /// which accepts none.
    /// </summary>
    /// <remarks>
    /// The silos which run the operations send requests to accepted URLs, so the application restricts them to receivers
    /// which it trusts, for example by scheme, host, and the authenticated caller. Deliveries also connect only to the
    /// addresses which <see cref="DurableCallbackOptions.AllowAddress"/> accepts, which the host's name resolves to when
    /// each delivery connects.
    /// </remarks>
    public Func<HttpContext, Uri, bool>? AllowCallback { get; set; }

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
    /// <para>
    /// Exception messages can describe internal state, such as hosts and grain identities, so the default describes only
    /// the outcome: <c>Canceled</c> for an <see cref="OperationCanceledException"/>, and <c>Failed</c> otherwise. An
    /// application which defines exceptions for its clients returns their details, for example:
    /// </para>
    /// <code>
    /// options.CreateError = exception => exception is OrderRejectedException rejected
    ///     ? new("OrderRejected", rejected.Message)
    ///     : DurableHttpOptions.DefaultCreateError(exception);
    /// </code>
    /// <para>
    /// An operation whose work's grain didn't accept the work, so the work never ran, fails with the
    /// <see cref="DurableOperationError.NotStartedCode"/> code, which this function doesn't describe.
    /// </para>
    /// </remarks>
    public Func<Exception, DurableOperationError> CreateError { get; set; } = DefaultCreateError;

    /// <summary>Describes an exception by the task's outcome alone, without its type or message.</summary>
    /// <param name="exception">The exception which failed or canceled the task.</param>
    /// <returns>An error whose code is <c>Canceled</c> or <c>Failed</c>.</returns>
    public static DurableOperationError DefaultCreateError(Exception exception) => exception is OperationCanceledException
        ? new("Canceled", "The operation was canceled.")
        : new("Failed", "The operation failed.");

    /// <summary>
    /// Returns the authenticated user's <see cref="ClaimTypes.NameIdentifier"/> claim, or its <c>sub</c> claim, or
    /// <see langword="null"/> for a request without either.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The caller's scope, or <see langword="null"/>.</returns>
    public static string? DefaultGetCallerScope(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        return user.Identity?.IsAuthenticated == true
            ? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value
            : null;
    }
}
