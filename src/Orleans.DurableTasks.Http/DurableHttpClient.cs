using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Orleans.DurableTasks.Http;

/// <summary>Runs HTTP requests and operations as steps of durable tasks with a named <see cref="HttpClient"/>.</summary>
/// <remarks>
/// The named client's handlers start with a <see cref="DurableIdempotencyKeyHandler"/>, which gives each step's unsafe
/// requests the step's <c>Idempotency-Key</c>.
/// </remarks>
internal sealed class DurableHttpClient(IHttpClientFactory clientFactory, string name, DurableHttpClientOptions options) : IDurableHttpClient
{
    /// <summary>The reconnection interval of an event stream which doesn't advertise one, as browsers' <c>EventSource</c> uses.</summary>
    private static readonly TimeSpan DefaultReconnectionInterval = TimeSpan.FromSeconds(3);

    private readonly JsonSerializerOptions _json = options.SerializerOptions;

    public DurableTask<DurableHttpResponse> SendAsync(Func<HttpRequestMessage> createRequest)
    {
        ArgumentNullException.ThrowIfNull(createRequest);
        return DurableTask.Run(
            static async (state, cancellationToken) =>
            {
                using var request = state.createRequest();
                using var response = await state.client.CreateClient().SendAsync(request, cancellationToken);
                return await DurableHttpResponse.ReadAsync(response, cancellationToken);
            },
            (client: this, createRequest));
    }

    public DurableTask<DurableHttpOperation> StartOperationAsync(Func<HttpRequestMessage> createRequest)
    {
        ArgumentNullException.ThrowIfNull(createRequest);
        return StartAsync(this, createRequest);

        static async DurableTask<DurableHttpOperation> StartAsync(DurableHttpClient client, Func<HttpRequestMessage> createRequest) =>
            DurableHttpOperation.FromStart(await client.SendAsync(createRequest));
    }

    public DurableTask<TResult> WaitForOperationAsync<TResult>(DurableHttpOperation operation, DurableHttpOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return WaitAsync<TResult>(operation, options ?? DurableHttpOperationOptions.Default);
    }

    public DurableTask<DurableOperation> GetOperationAsync(Uri operationLocation)
    {
        ArgumentNullException.ThrowIfNull(operationLocation);
        return ReadOperationAsync(this, () => new HttpRequestMessage(HttpMethod.Get, operationLocation));
    }

    public DurableTask<DurableOperation> CancelOperationAsync(Uri operationLocation, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(operationLocation);
        var cancelUri = Append(operationLocation, ":cancel");
        return ReadOperationAsync(this, () => new HttpRequestMessage(HttpMethod.Post, cancelUri)
        {
            Content = JsonContent.Create(new DurableOperationCancelRequest(reason), DurableHttpJsonContext.Default.DurableOperationCancelRequest),
        });
    }

    public DurableTask SignalOperationAsync<T>(Uri operationLocation, string signalName, T value)
    {
        ArgumentNullException.ThrowIfNull(operationLocation);
        ArgumentException.ThrowIfNullOrEmpty(signalName);
        var signalUri = Append(operationLocation, $"/signals/{Uri.EscapeDataString(signalName)}");
        return SignalAsync(this, () => new HttpRequestMessage(HttpMethod.Post, signalUri) { Content = JsonContent.Create(value, _json.GetTypeInfo<T>()) });

        static async DurableTask SignalAsync(DurableHttpClient client, Func<HttpRequestMessage> createRequest) =>
            (await client.SendAsync(createRequest)).EnsureSuccessStatusCode();
    }

    public async IAsyncEnumerable<DurableOperation> WatchOperationAsync(Uri operationLocation, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLocation);
        var eventsUri = Append(operationLocation, "/events");
        var client = CreateClient();
        string? lastEventId = null;
        var reconnectionInterval = DefaultReconnectionInterval;
        while (true)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, eventsUri))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (lastEventId is not null)
                {
                    request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
                }

                HttpResponseMessage? response;
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (HttpRequestException exception) when (exception.StatusCode is null && !cancellationToken.IsCancellationRequested)
                {
                    // The server is unreachable, so reconnect after the interval, as an EventSource does.
                    response = null;
                }

                if (response is not null)
                {
                    using (response)
                    {
                        // The server answers a reconnect after the terminal status with 204 No Content.
                        if (response.StatusCode == HttpStatusCode.NoContent)
                        {
                            yield break;
                        }

                        response.EnsureSuccessStatusCode();
                        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        var parser = SseParser.Create(stream, static (eventType, data) => eventType == "status" ? DurableOperation.Read(data) : null);
                        await using var events = parser.EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                        while (await MoveNextAsync(events, cancellationToken))
                        {
                            if (events.Current.Data is not { } operation)
                            {
                                continue;
                            }

                            yield return operation;
                            if (operation.IsTerminal)
                            {
                                yield break;
                            }
                        }

                        // The stream ended before the operation's terminal status, such as when a proxy closed it, so
                        // reconnect with the last event's identifier, after the interval which the server advertises.
                        lastEventId = parser.LastEventId is { Length: > 0 } id ? id : lastEventId;
                        reconnectionInterval = parser.ReconnectionInterval is var interval && interval > TimeSpan.Zero && interval != Timeout.InfiniteTimeSpan
                            ? interval
                            : reconnectionInterval;
                    }
                }
            }

            await Task.Delay(reconnectionInterval, cancellationToken);
        }

        // Reads the next event, or returns false once the stream ends, including when the connection drops mid-stream.
        static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<SseItem<DurableOperation?>> events, CancellationToken cancellationToken)
        {
            try
            {
                return await events.MoveNextAsync();
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private HttpClient CreateClient() => clientFactory.CreateClient(name);

    private async DurableTask<TResult> WaitAsync<TResult>(DurableHttpOperation operation, DurableHttpOperationOptions options)
    {
        if (operation.IsCompleted)
        {
            return TryComplete(operation.Response, isStatusMonitorResponse: false, out TResult completed)
                ? completed
                : throw new UnreachableException("A completed start response carries its result or its failure.");
        }

        // A running operation always has a location, which FromStart establishes.
        var location = operation.Location!;
        if (options.Mode == DurableHttpWaitMode.Events && operation.IsStatusMonitor)
        {
            var terminal = await DurableTask.Run(
                static async (state, cancellationToken) =>
                {
                    await foreach (var status in state.client.WatchOperationAsync(state.location, cancellationToken))
                    {
                        if (status.IsTerminal)
                        {
                            return status;
                        }
                    }

                    throw new HttpRequestException($"The events of '{state.location}' ended before the operation's terminal status.");
                },
                (client: this, location));
            return ReadResult<TResult>(terminal);
        }

        var response = operation.Response;
        while (true)
        {
            if (RetryAt(response) is { } retryAt)
            {
                await DurableTask.DelayUntil(retryAt);
            }
            else
            {
                await DurableTask.Delay(RetryAfter(response) ?? options.PollInterval);
            }

            response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, location);
                if (options.Wait > TimeSpan.Zero)
                {
                    request.Headers.TryAddWithoutValidation(DurableHttpHeaders.Prefer, $"wait={(long)options.Wait.TotalSeconds}");
                }

                return request;
            });
            if (TryComplete(response, operation.IsStatusMonitor, out TResult result))
            {
                return result;
            }
        }
    }

    /// <summary>Reads the result of a response which completes the operation, or returns <see langword="false"/> while it runs.</summary>
    private bool TryComplete<TResult>(DurableHttpResponse response, bool isStatusMonitorResponse, out TResult result)
    {
        result = default!;
        if (isStatusMonitorResponse && response.IsSuccessStatusCode)
        {
            // A status monitor reports the operation in its body, whichever success status code answers a poll of it.
            var operation = DurableOperation.Read(response.Content.Span);
            if (!operation.IsTerminal)
            {
                return false;
            }

            result = ReadResult<TResult>(operation);
            return true;
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return false;
        }

        if (!isStatusMonitorResponse && response.IsSuccessStatusCode)
        {
            result = response.ReadFromJson<TResult>(_json)!;
            return true;
        }

        if (!isStatusMonitorResponse && response.StatusCode == HttpStatusCode.FailedDependency)
        {
            result = ReadResult<TResult>(DurableOperation.Read(response.Content.Span));
            return true;
        }

        throw response.CreateException();
    }

    private TResult ReadResult<TResult>(DurableOperation operation) => operation.Status switch
    {
        DurableOperationStatus.Succeeded => operation.Result is { ValueKind: not JsonValueKind.Null } result ? result.Deserialize(_json.GetTypeInfo<TResult>())! : default!,
        var status => throw new DurableHttpOperationException(
            status is DurableOperationStatus.Canceled ? status : DurableOperationStatus.Failed,
            operation.Error?.Code,
            operation.Error?.Message),
    };

    private static async DurableTask<DurableOperation> ReadOperationAsync(DurableHttpClient client, Func<HttpRequestMessage> createRequest)
    {
        var response = (await client.SendAsync(createRequest)).EnsureSuccessStatusCode();
        return DurableOperation.Read(response.Content.Span);
    }

    private static TimeSpan? RetryAfter(DurableHttpResponse response) =>
        RetryConditionHeaderValue.TryParse(response.GetHeader("Retry-After"), out var value) ? value.Delta : null;

    private static DateTimeOffset? RetryAt(DurableHttpResponse response) =>
        RetryConditionHeaderValue.TryParse(response.GetHeader("Retry-After"), out var value) ? value.Date : null;

    /// <summary>Appends <paramref name="suffix"/> to the path of <paramref name="location"/>, keeping its query.</summary>
    private static Uri Append(Uri location, string suffix) => new(location.GetLeftPart(UriPartial.Path) + suffix + location.Query);
}
