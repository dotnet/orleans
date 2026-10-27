namespace Orleans.DurableTasks.Http;

/// <summary>
/// Sends HTTP requests and runs HTTP operations as steps of durable tasks, so retries and replays of a task reach the
/// same server-side work.
/// </summary>
/// <remarks>
/// <para>
/// Each method which returns a durable task records its responses as steps of the calling task: a replay reads the
/// recorded response without sending the request again, and a step which a failure or a deactivation interrupts sends
/// its request again with the same <c>Idempotency-Key</c>, which identifies the step. Requests with safe methods, such as
/// <c>GET</c>, carry no key. Call these methods from durable executions, such as durable grain methods, in silos.
/// </para>
/// <para>
/// Operations follow the conventions which this package's durable operation endpoints serve, which are compatible with
/// Azure long-running operations: a <c>202 Accepted</c> response locates the operation's status monitor with
/// <c>Operation-Location</c>, which <c>POST {status monitor}:cancel</c> cancels and
/// <c>POST {status monitor}/signals/{name}</c> signals. <see cref="WaitForOperationAsync{TResult}"/> also waits for
/// operations which Azure services, and servers which answer <c>202 Accepted</c> with a <c>Location</c>, run.
/// </para>
/// <para>
/// Register a client with <c>AddDurableHttpClient</c>, which returns the builder of its <see cref="HttpClient"/>, and
/// inject it as a keyed service with the client's name.
/// </para>
/// </remarks>
public interface IDurableHttpClient
{
    /// <summary>Sends the request which <paramref name="createRequest"/> creates, as a step of the calling task.</summary>
    /// <param name="createRequest">Creates the request; it runs for each attempt of the step.</param>
    /// <returns>A durable task which returns the recorded response, whichever its status code.</returns>
    DurableTask<DurableHttpResponse> SendAsync(Func<HttpRequestMessage> createRequest);

    /// <summary>Sends the request which starts an operation, as a step of the calling task, and returns the operation.</summary>
    /// <param name="createRequest">Creates the request; it runs for each attempt of the step.</param>
    /// <returns>A durable task which returns the operation, which the start response may already have completed.</returns>
    /// <exception cref="HttpRequestException">
    /// The response reports neither an operation nor a result, or accepts the request without locating its operation.
    /// </exception>
    DurableTask<DurableHttpOperation> StartOperationAsync(Func<HttpRequestMessage> createRequest);

    /// <summary>Durably waits for an operation's result.</summary>
    /// <typeparam name="TResult">The type which the operation's JSON result deserializes to.</typeparam>
    /// <param name="operation">The operation which <see cref="StartOperationAsync"/> returned.</param>
    /// <param name="options">Configures the wait, or <see langword="null"/> for the defaults.</param>
    /// <returns>A durable task which returns the operation's result.</returns>
    /// <remarks>
    /// <para>
    /// A start response which <see cref="DurableHttpOperation.IsCompleted"/> reports carries the result, which a
    /// <c>204 No Content</c> response leaves <see langword="default"/>, or, with <c>424 Failed Dependency</c>, the status
    /// monitor of a failed or canceled operation. A status monitor reports <c>status</c> and, once it is terminal,
    /// <c>result</c> or <c>error</c>; a <c>Location</c> answers <c>202 Accepted</c> while the operation runs and the
    /// result once it succeeds.
    /// </para>
    /// <para>
    /// Polls wait for the interval which <c>Retry-After</c> advertises, or <see cref="DurableHttpOperationOptions.PollInterval"/>,
    /// as durable delays. Each poll records a delay and a step, which the task's deletion removes, so prefer intervals
    /// which suit the operation's expected duration, or <see cref="DurableHttpWaitMode.Events"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="DurableHttpOperationException">The operation failed or was canceled.</exception>
    /// <exception cref="HttpRequestException">A response has a status code which reports neither a status nor a result.</exception>
    DurableTask<TResult> WaitForOperationAsync<TResult>(DurableHttpOperation operation, DurableHttpOperationOptions? options = null);

    /// <summary>Reads an operation's status monitor, as a step of the calling task.</summary>
    /// <param name="operationLocation">The URL of the operation's status monitor.</param>
    /// <returns>A durable task which returns the operation's status, and its result or error once it is terminal.</returns>
    /// <exception cref="HttpRequestException">The response doesn't have a success status code.</exception>
    DurableTask<DurableOperation> GetOperationAsync(Uri operationLocation);

    /// <summary>Requests cancellation of an operation with <c>POST {status monitor}:cancel</c>, as a step of the calling task.</summary>
    /// <param name="operationLocation">The URL of the operation's status monitor.</param>
    /// <param name="reason">The reason which the operation's task reads, or <see langword="null"/> for none.</param>
    /// <returns>
    /// A durable task which returns the operation's status: running while its cancellation proceeds, or terminal.
    /// </returns>
    /// <exception cref="HttpRequestException">The response doesn't have a success status code.</exception>
    DurableTask<DurableOperation> CancelOperationAsync(Uri operationLocation, string? reason = null);

    /// <summary>
    /// Delivers a signal with a JSON value to an operation with <c>POST {status monitor}/signals/{name}</c>, as a step of
    /// the calling task.
    /// </summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="operationLocation">The URL of the operation's status monitor.</param>
    /// <param name="signalName">The signal's name.</param>
    /// <param name="value">The signal's value.</param>
    /// <returns>A durable task which completes once the operation holds the signal.</returns>
    /// <exception cref="HttpRequestException">
    /// The response doesn't have a success status code, such as <c>409 Conflict</c> for an operation which holds another
    /// value or completed without the signal.
    /// </exception>
    DurableTask SignalOperationAsync<T>(Uri operationLocation, string signalName, T value);

    /// <summary>
    /// Streams an operation's statuses from the server-sent events of <c>{status monitor}/events</c> until the operation
    /// is terminal.
    /// </summary>
    /// <param name="operationLocation">The URL of the operation's status monitor.</param>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The operation's statuses, which end with its terminal status.</returns>
    /// <remarks>
    /// <para>
    /// A stream which ends or drops before the terminal status, or a server which is unreachable, reconnects after the
    /// interval which the server advertises, with the last event's identifier, as an <c>EventSource</c> does. A reconnect
    /// reports the operation's current status, which can repeat the last status.
    /// </para>
    /// <para>
    /// The stream is not a durable step: read it outside durable executions, such as in a request handler, or within a
    /// step, which <see cref="DurableHttpWaitMode.Events"/> does.
    /// </para>
    /// </remarks>
    /// <exception cref="HttpRequestException">A response doesn't have a success status code.</exception>
    IAsyncEnumerable<DurableOperation> WatchOperationAsync(Uri operationLocation, CancellationToken cancellationToken = default);
}

/// <summary>Composes the operations of an <see cref="IDurableHttpClient"/>.</summary>
public static class DurableHttpClientExtensions
{
    /// <summary>
    /// Starts an operation with the request which <paramref name="createRequest"/> creates, and durably waits for its
    /// result.
    /// </summary>
    /// <typeparam name="TResult">The type which the operation's JSON result deserializes to.</typeparam>
    /// <param name="client">The client.</param>
    /// <param name="createRequest">Creates the request which starts the operation; it runs for each attempt of the start.</param>
    /// <param name="options">Configures the wait, or <see langword="null"/> for the defaults.</param>
    /// <returns>A durable task which returns the operation's result.</returns>
    /// <exception cref="DurableHttpOperationException">The operation failed or was canceled.</exception>
    /// <exception cref="HttpRequestException">A response reports neither an operation nor a result.</exception>
    public static DurableTask<TResult> RunOperationAsync<TResult>(
        this IDurableHttpClient client,
        Func<HttpRequestMessage> createRequest,
        DurableHttpOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(createRequest);
        return RunAsync(client, createRequest, options);

        static async DurableTask<TResult> RunAsync(IDurableHttpClient client, Func<HttpRequestMessage> createRequest, DurableHttpOperationOptions? options)
        {
            var operation = await client.StartOperationAsync(createRequest);
            return await client.WaitForOperationAsync<TResult>(operation, options);
        }
    }
}
