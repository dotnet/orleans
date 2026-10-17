namespace Orleans.DurableTasks.Http;

/// <summary>Names the HTTP headers of the durable HTTP operation protocol.</summary>
public static class DurableHttpHeaders
{
    /// <summary>
    /// The request header which identifies a request to start an operation, so retries of the request start it once.
    /// </summary>
    /// <remarks>
    /// The value follows Stripe and the IETF <c>Idempotency-Key</c> draft: up to 255 printable ASCII characters, sent
    /// bare or as a quoted structured-field string.
    /// </remarks>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>The response header which locates an operation's status monitor, as Azure long-running operations do.</summary>
    public const string OperationLocation = "Operation-Location";

    /// <summary>
    /// The request header which registers a callback URL, which receives the operation's status monitor once the operation
    /// is terminal. Each delivery carries the request's headers which start with <see cref="OperationCallbackHeaderPrefix"/>,
    /// without the prefix.
    /// </summary>
    public const string OperationCallback = "Operation-Callback";

    /// <summary>The prefix of request headers which each callback delivery carries, without the prefix.</summary>
    public const string OperationCallbackHeaderPrefix = "Operation-Callback-";
    internal const string Prefer = "Prefer";
    internal const string PreferenceApplied = "Preference-Applied";
}
