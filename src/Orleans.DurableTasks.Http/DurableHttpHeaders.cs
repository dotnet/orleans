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

    internal const string Prefer = "Prefer";
    internal const string PreferenceApplied = "Preference-Applied";
}
