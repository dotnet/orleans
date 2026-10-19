using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Orleans.DurableTasks.Http;

/// <summary>
/// Serves durable operations with the Azure long-running operation convention: status monitors under
/// <c>{group}/operations/{operationId}</c>, <c>Operation-Location</c>, <c>Retry-After</c>, and <c>Prefer: wait</c>.
/// </summary>
internal sealed class DurableHttpOperations(
    DurableOperationService operations,
    DurableCallbackRegistrar callbacks,
    TemplateBinderFactory binders,
    EndpointDataSource endpoints,
    IOptions<DurableHttpOptions> options,
    IOptions<HttpJsonOptions> jsonOptions)
{
    /// <summary>The route parameter which names an operation.</summary>
    public const string OperationIdParameter = "operationId";

    /// <summary>The type of the server-sent events which carry an operation's status.</summary>
    private const string StatusEvent = "status";

    /// <summary>The type of the server-sent events which keep an event stream's connection alive, without data.</summary>
    private const string PingEvent = "ping";

    private readonly DurableHttpOptions _options = options.Value;
    private readonly JsonSerializerOptions _json = jsonOptions.Value.SerializerOptions;

    public DurableHttpOptions Options => _options;

    /// <summary>
    /// Starts <paramref name="work"/> as the operation <paramref name="operationId"/>, waits for it as the request prefers,
    /// and returns its result or its status monitor. A request which registers a callback with an
    /// <c>Operation-Callback</c> header receives the status monitor there once a running operation is terminal.
    /// </summary>
    public async Task<IResult> StartAsync(HttpContext context, DurableOperationsMetadata group, DurableTask work, string operationId)
    {
        if (!callbacks.TryRead(
            context,
            context.Request.Headers[DurableHttpHeaders.OperationCallback].ToString(),
            DurableHttpHeaders.OperationCallbackHeaderPrefix,
            out var callback,
            out var callbackError))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Operation-Callback is not accepted", detail: callbackError);
        }

        DurableTaskResponse response;
        var key = group.GetKey(context, endpoints, _options, operationId);
        var wait = DurableHttpRequestHeaders.GetWait(context.Request.Headers, _options.MaxWait);
        try
        {
            response = await operations.StartAsync(key, work, wait, context.RequestAborted);
        }
        catch (DurableTaskConflictException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency-Key is already used",
                detail: "The Idempotency-Key identifies an operation which another request started. A key is reused only to retry the same request.");
        }
        catch (DurableOperationUnavailableException)
        {
            return Unavailable(context);
        }

        var location = GetOperationLocation(context, group, operationId);
        context.Response.Headers[DurableHttpHeaders.OperationLocation] = location;
        if (callback is not null && !response.IsCompleted)
        {
            await callbacks.RegisterAsync(callback, DurableCallbackProtocol.Webhook, operations.GetHostId(key), operationId, location, context.RequestAborted);
        }

        if (wait > TimeSpan.Zero)
        {
            context.Response.Headers[DurableHttpHeaders.PreferenceApplied] = $"wait={(int)wait.TotalSeconds}";
        }

        return response.Status switch
        {
            DurableTaskStatus.CompletedSuccessfully when response.ResultType is { } resultType =>
                Results.Json(response.Result, _json.GetTypeInfo(resultType)),
            DurableTaskStatus.CompletedSuccessfully => TypedResults.NoContent(),
            DurableTaskStatus.Failed or DurableTaskStatus.Canceled =>
                OperationResult(context, operationId, response, StatusCodes.Status424FailedDependency),
            _ => OperationResult(context, operationId, response, StatusCodes.Status202Accepted),
        };
    }

    /// <summary>Returns the operation's status monitor once it is terminal or the request's preferred wait has elapsed.</summary>
    public Task<IResult> GetAsync(HttpContext context) => HandleAsync(context, async (operation, cancellationToken) =>
    {
        var wait = DurableHttpRequestHeaders.GetWait(context.Request.Headers, _options.MaxWait);
        var response = await operations.PollAsync(operation.Key, wait, cancellationToken);
        return OperationResult(context, operation.Id, response, StatusCodes.Status200OK);
    });

    /// <summary>
    /// Streams the operation's status as server-sent events: its current status, then its terminal status once it is
    /// running, after which the stream ends. Each event's identifier is its status.
    /// </summary>
    /// <remarks>
    /// An <c>EventSource</c> reconnects when a stream ends, carrying the last event's identifier, so a reconnect after the
    /// terminal status receives <c>204 No Content</c>, which ends its reconnects.
    /// </remarks>
    public Task<IResult> WatchAsync(HttpContext context) => HandleAsync(context, async (operation, cancellationToken) =>
    {
        if (Enum.TryParse<DurableOperationStatus>(context.Request.Headers["Last-Event-ID"], out var lastStatus)
            && lastStatus is DurableOperationStatus.Succeeded or DurableOperationStatus.Failed or DurableOperationStatus.Canceled)
        {
            return TypedResults.NoContent();
        }

        var response = await operations.PollAsync(operation.Key, TimeSpan.Zero, cancellationToken);
        return new DurableOperationEventsResult(WatchAsync(operation, response, cancellationToken));
    });

    /// <summary>
    /// Durably requests cancellation of a running operation, carrying the optional <c>reason</c> of the request's JSON body,
    /// and returns its status monitor: <c>202 Accepted</c> for a running operation, and <c>200 OK</c> for a terminal one,
    /// which is unchanged.
    /// </summary>
    public async Task<IResult> CancelAsync(HttpContext context)
    {
        var (request, error) = await ReadBodyAsync(context, DurableHttpJsonContext.Default.DurableOperationCancelRequest, isRequired: false);
        return error ?? await HandleAsync(context, async (operation, cancellationToken) =>
        {
            var response = await operations.CancelAsync(operation.Key, request?.Reason, cancellationToken);
            return OperationResult(context, operation.Id, response, response.IsCompleted ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        });
    }

    /// <summary>Delivers <paramref name="signal"/> with the value of the request's JSON body to the operation's task.</summary>
    public async Task<IResult> SignalAsync<T>(HttpContext context, DurableSignal<T> signal)
    {
        var (value, error) = await ReadBodyAsync(context, _json.GetTypeInfo<T>(), isRequired: true);
        return error ?? await HandleAsync(context, async (operation, cancellationToken) =>
            await operations.SignalAsync(operation.Key, signal, value!, cancellationToken) switch
            {
                DurableSignalResult.Accepted or DurableSignalResult.Duplicate => TypedResults.NoContent(),
                DurableSignalResult.TaskNotFound => OperationNotFound(),
                DurableSignalResult.TaskCompleted => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The operation has completed",
                    detail: $"The operation completed without receiving signal '{signal.Name}'."),
                var result => TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The signal has another value",
                    detail: $"The operation holds a different value for signal '{signal.Name}' ({result})."),
            });
    }

    /// <summary>
    /// Reads the request's JSON body as a Minimal API binds a <c>[FromBody]</c> parameter: a body of another content type
    /// returns <c>415 Unsupported Media Type</c>, and a body which isn't valid JSON, or a required body which is missing or
    /// holds <c>null</c>, returns <c>400 Bad Request</c>.
    /// </summary>
    /// <remarks>
    /// Whether the request has a body decides whether a value was sent, so a missing body never reads as the type's
    /// default, such as <see langword="false"/> for a <see cref="bool"/>.
    /// </remarks>
    private static async Task<(T? Value, IResult? Error)> ReadBodyAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo, bool isRequired)
    {
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true)
        {
            return isRequired ? (default, BadBody($"The request has no body, which holds the {typeof(T).Name} value.")) : (default, null);
        }

        if (!context.Request.HasJsonContentType())
        {
            return (default, TypedResults.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "The request body isn't JSON",
                detail: "The request's Content-Type is application/json, or another JSON media type."));
        }

        T? value;
        try
        {
            value = await context.Request.ReadFromJsonAsync(typeInfo, context.RequestAborted);
        }
        catch (JsonException)
        {
            return (default, BadBody($"The request body isn't a valid JSON {typeof(T).Name}."));
        }

        return value is null && isRequired ? (default, BadBody($"The request body holds no {typeof(T).Name} value.")) : (value, null);

        static IResult BadBody(string detail) =>
            TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The request body is invalid", detail: detail);
    }

    /// <summary>
    /// Handles a request which names an operation with <paramref name="handler"/>, or returns <c>404 Not Found</c> for an
    /// operation which doesn't exist, and <c>503 Service Unavailable</c> for one which the request didn't reach.
    /// </summary>
    private async Task<IResult> HandleAsync(HttpContext context, Func<Operation, CancellationToken, Task<IResult>> handler)
    {
        if (context.GetRouteValue(OperationIdParameter) is not string operationId || !DurableHttpRequestHeaders.IsValidOperationId(operationId))
        {
            return OperationNotFound();
        }

        try
        {
            var key = DurableOperationsMetadata.Get(context).GetKey(context, endpoints, _options, operationId);
            return await handler(new(operationId, key), context.RequestAborted);
        }
        catch (DurableTaskNotFoundException)
        {
            return OperationNotFound();
        }
        catch (DurableOperationUnavailableException)
        {
            return Unavailable(context);
        }
    }

    private async IAsyncEnumerable<SseItem<DurableOperation>> WatchAsync(
        Operation operation,
        DurableTaskResponse response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var status = ToOperation(operation.Id, response);
        yield return new(status, StatusEvent) { EventId = status.Status.ToString(), ReconnectionInterval = _options.RetryAfter };
        if (status.IsTerminal)
        {
            yield break;
        }

        while (true)
        {
            response = await operations.PollAsync(operation.Key, DurableOperationService.MaxPollInterval, cancellationToken);
            if (response.IsCompleted)
            {
                break;
            }

            // A heartbeat after each poll writes to the connection, so a stream which its client abandoned ends with the
            // write, rather than polling for the operation's duration.
            yield return new(status, PingEvent);
        }

        status = ToOperation(operation.Id, response);
        yield return new(status, StatusEvent) { EventId = status.Status.ToString() };
    }

    private IResult OperationResult(HttpContext context, string operationId, DurableTaskResponse response, int statusCode)
    {
        if (!response.IsCompleted)
        {
            SetRetryAfter(context);
        }

        return TypedResults.Json(ToOperation(operationId, response), DurableHttpJsonContext.Default.DurableOperation, statusCode: statusCode);
    }

    private DurableOperation ToOperation(string operationId, DurableTaskResponse response) =>
        DurableOperationResponses.ToOperation(operationId, response, _json, _options);

    /// <summary>
    /// Returns <c>503 Service Unavailable</c> for a request which left its operation as it was, such as one which didn't
    /// reach the operation's grains, with a <c>Retry-After</c> header, so the client sends it again.
    /// </summary>
    private IResult Unavailable(HttpContext context)
    {
        SetRetryAfter(context);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The operation is unavailable",
            detail: "The request didn't reach the operation, which it left unchanged. Send the request again with the same Idempotency-Key.");
    }

    private void SetRetryAfter(HttpContext context) =>
        context.Response.Headers.RetryAfter = ((long)Math.Ceiling(_options.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the URL of the operation's status monitor, in the group of the request's endpoint, with the request's values
    /// for the group's route parameters.
    /// </summary>
    private string GetOperationLocation(HttpContext context, DurableOperationsMetadata group, string operationId)
    {
        var path = group.GetOperationPath(context, endpoints, binders, operationId)
            ?? throw new InvalidOperationException(
                $"The route values of '{context.GetEndpoint()?.DisplayName}' don't locate its group's operations. A durable task endpoint uses the route parameters of the group which WithDurableOperations configures.");
        var request = context.Request;
        return UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, new PathString(path));
    }
    private static IResult OperationNotFound() => TypedResults.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "The operation was not found",
        detail: "The operation doesn't exist, or its retention has ended.");

    /// <summary>Identifies the operation which a request names.</summary>
    /// <param name="Id">The operation identifier, which the request names.</param>
    /// <param name="Key">The operation's key.</param>
    private readonly record struct Operation(string Id, string Key);

    /// <summary>Writes operation statuses as a <c>text/event-stream</c> response.</summary>
    private sealed class DurableOperationEventsResult(IAsyncEnumerable<SseItem<DurableOperation>> events) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache,no-store";
            response.Headers.Pragma = "no-cache";
            response.Headers.ContentEncoding = "identity";
            httpContext.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();
            await SseFormatter.WriteAsync(
                events,
                response.Body,
                static (item, writer) =>
                {
                    if (item.EventType == PingEvent)
                    {
                        return;
                    }

                    using var json = new Utf8JsonWriter(writer);
                    JsonSerializer.Serialize(json, item.Data, DurableHttpJsonContext.Default.DurableOperation);
                },
                httpContext.RequestAborted);
        }
    }
}
