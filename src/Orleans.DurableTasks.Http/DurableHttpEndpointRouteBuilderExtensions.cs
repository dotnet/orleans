using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Makes route groups serve durable operations: long-running HTTP operations which durable tasks perform.</summary>
/// <remarks>
/// <para>
/// <see cref="WithDurableOperations"/> makes each endpoint of a group whose handler returns a durable grain call, such as
/// <c>grain.PlaceOrderAsync(order)</c>, or a reference to an existing task, such as
/// <c>grain.GetDurableTask&lt;T&gt;(id)</c>, start an operation which performs it, and maps the endpoints which observe,
/// cancel, and signal the group's operations. Other endpoints of the group are unchanged.
/// </para>
/// <para>
/// An operation is named by the <c>Idempotency-Key</c> of the request which starts it, so a retried request reaches the
/// operation which the first request started. Its key combines the group's scope, the request's values for the group's
/// route parameters, such as an order's identifier, the caller's scope, which
/// <see cref="DurableHttpOptions.GetCallerScope"/> returns, and the identifier. An operation host grain, which that key
/// addresses, holds the operation, so requests which observe, cancel, or signal it reach it by its identifier alone, and
/// only from its caller's scope. Authentication and authorization belong to the application, which configures them on the
/// group's endpoints, such as with <c>RequireAuthorization</c> or endpoint filters.
/// </para>
/// </remarks>
public static class DurableHttpEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Makes the group's endpoints which return durable work start operations, and maps the endpoints which serve the
    /// group's operations under <c>{group}/operations</c>.
    /// </summary>
    /// <param name="group">The route group, such as <c>app.MapGroup("/orders/{orderId}")</c>.</param>
    /// <param name="scope">
    /// The scope of the group's operations, such as <c>orders</c>, which is part of every operation's key, so it stays
    /// equal while the group's routes change. Groups, and Nexus services, with equal scopes and route values address the
    /// same operations.
    /// </param>
    /// <returns>The group, which maps the group's durable starts and signals, and configures its endpoints.</returns>
    /// <remarks>
    /// <para>A start, such as <c>POST {group}/place</c>, whose handler returns durable work, responds:</para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>200 OK</c> with the result, or <c>204 No Content</c> for work without one, when the operation has succeeded,
    /// including within the wait which a <c>Prefer: wait=N</c> header requests.
    /// </description></item>
    /// <item><description>
    /// <c>202 Accepted</c> with the operation's status monitor while it runs, including for a retry which arrives before it
    /// completes: the retry reaches the running operation, as Azure repeatable requests and Stripe's v2 idempotency resume
    /// work, where the IETF <c>Idempotency-Key</c> draft returns <c>409 Conflict</c>.
    /// </description></item>
    /// <item><description>
    /// <c>424 Failed Dependency</c> with the status monitor of an operation which failed or was canceled. An operation whose
    /// work's grain refused the work without storing it fails with the <see cref="DurableOperationError.NotStartedCode"/>
    /// error code, and a new key starts the work again. An operation whose work's grain didn't confirm the work fails with
    /// the <see cref="DurableOperationError.OutcomeUnknownCode"/> error code: the work may have run, so a client checks its
    /// effects before it starts the work again.
    /// </description></item>
    /// <item><description>
    /// <c>400 Bad Request</c> for an invalid or missing key, and <c>422 Unprocessable Content</c> for a key which another
    /// request used to start other work: a call of another method or on another grain, or a subscription to another
    /// task. A retry whose arguments differ reaches the operation, as the durable task runtime identifies operations.
    /// </description></item>
    /// <item><description>
    /// <c>503 Service Unavailable</c> with a <c>Retry-After</c> header when the request didn't reach the operation, which it
    /// left unchanged, so the client sends it again with the same key.
    /// </description></item>
    /// </list>
    /// <para>
    /// Each response which identifies an operation carries an <c>Operation-Location</c> header with its status monitor's
    /// URL. The handler runs for each request, including retries, and only creates the work; the endpoint starts it.
    /// </para>
    /// <para>The group serves its operations:</para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>GET {group}/operations/{operationId}</c> returns the status monitor. A <c>Prefer: wait=N</c> header long-polls: the
    /// response waits until the operation is terminal or the wait elapses. A running operation's response carries a
    /// <c>Retry-After</c> header.
    /// </description></item>
    /// <item><description>
    /// <c>GET {group}/operations/{operationId}/events</c> streams the status monitor as server-sent <c>status</c> events
    /// whose identifiers are their statuses: the current status, then the terminal status of a running operation, after
    /// which the stream ends. A reconnect whose <c>Last-Event-ID</c> is a terminal status returns <c>204 No Content</c>,
    /// which ends an <c>EventSource</c>'s reconnects.
    /// </description></item>
    /// <item><description>
    /// <c>POST {group}/operations/{operationId}:cancel</c> durably requests cancellation of a running operation, carrying
    /// the optional <c>reason</c> of a JSON body, and returns <c>202 Accepted</c> with its status monitor; a terminal
    /// operation returns <c>200 OK</c> unchanged. An operation which subscribes to an existing task ends its subscription
    /// and reports that it was canceled, and the task, which other parties own, continues.
    /// </description></item>
    /// </list>
    /// <para>An operation which doesn't exist, or whose retention has ended, returns <c>404 Not Found</c>.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is empty.</exception>
    public static RouteGroupBuilder WithDurableOperations(this RouteGroupBuilder group, string scope)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrEmpty(scope);
        var metadata = new DurableOperationsMetadata(scope);
        group.WithMetadata(metadata);
        group.AddEndpointFilterFactory(static (context, next) =>
            DurableTaskEndpointConventions.GetResultType(context.MethodInfo) is null ? next : invocation => StartAsync(invocation, next));
        ((IEndpointConventionBuilder)group).Finally(static endpoint => DurableTaskEndpointConventions.DescribeStart(endpoint));

        group.MapGet("/operations/{operationId}", static context => ExecuteAsync(context, Operations(context).GetAsync(context)))
            .WithMetadata(new DurableOperationStatusEndpoint(metadata))
            .WithResponse(StatusCodes.Status200OK, typeof(DurableOperation))
            .WithProblem(StatusCodes.Status404NotFound)
            .WithProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/operations/{operationId}/events", static context => ExecuteAsync(context, Operations(context).WatchAsync(context)))
            .WithResponse(StatusCodes.Status200OK, typeof(DurableOperation), "text/event-stream")
            .WithResponse(StatusCodes.Status204NoContent)
            .WithProblem(StatusCodes.Status404NotFound);

        group.MapPost("/operations/{operationId}:cancel", static context => ExecuteAsync(context, Operations(context).CancelAsync(context)))
            .WithMetadata(new AcceptsMetadata(["application/json"], typeof(DurableOperationCancelRequest), isOptional: true))
            .WithResponse(StatusCodes.Status202Accepted, typeof(DurableOperation))
            .WithResponse(StatusCodes.Status200OK, typeof(DurableOperation))
            .WithProblem(StatusCodes.Status404NotFound);

        return group;
    }

    /// <summary>
    /// Maps <c>POST {group}/operations/{operationId}/signals/{name}</c>, in a group which
    /// <see cref="WithDurableOperations"/> configures, which delivers <paramref name="signal"/> with the JSON request body
    /// as its value to an operation's work.
    /// </summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="endpoints">The group.</param>
    /// <param name="signal">The signal, whose name completes the route.</param>
    /// <returns>A builder which configures the endpoint.</returns>
    /// <remarks>
    /// The endpoint returns <c>204 No Content</c> once the work's task durably holds the value, including for a repeated
    /// signal with an equal value, so a client retries a signal safely. A request without a body returns
    /// <c>400 Bad Request</c>. A value which differs from the one the task holds, or an operation which completed without
    /// the signal, returns <c>409 Conflict</c>, and an unknown operation returns <c>404 Not Found</c>. A request which
    /// reaches an operation before its host records its work returns <c>503 Service Unavailable</c> with a
    /// <c>Retry-After</c> header.
    /// </remarks>
    /// <exception cref="ArgumentException">The signal's name has characters other than URL-unreserved ones.</exception>
    public static IEndpointConventionBuilder MapDurableSignal<T>(this IEndpointRouteBuilder endpoints, DurableSignal<T> signal)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(signal);
        DurableTaskEndpointConventions.ThrowIfInvalidName(signal.Name, nameof(signal));
        return endpoints.MapPost(
                $"/operations/{{operationId}}/signals/{signal.Name}",
                context => ExecuteAsync(context, Operations(context).SignalAsync(context, signal)))
            .WithMetadata(new AcceptsMetadata(["application/json"], typeof(T)))
            .WithResponse(StatusCodes.Status204NoContent)
            .WithProblem(StatusCodes.Status400BadRequest)
            .WithProblem(StatusCodes.Status404NotFound)
            .WithProblem(StatusCodes.Status409Conflict)
            .WithProblem(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Executes the <paramref name="result"/> of an operation endpoint's handler.</summary>
    /// <remarks>
    /// The endpoints which serve operations are request delegates, which bind their requests without reflection, so they
    /// serve trimmed and NativeAOT applications.
    /// </remarks>
    internal static async Task ExecuteAsync(HttpContext context, Task<IResult> result) => await (await result).ExecuteAsync(context);

    private static DurableHttpOperations Operations(HttpContext context) => context.RequestServices.GetRequiredService<DurableHttpOperations>();

    internal static TBuilder WithResponse<TBuilder>(this TBuilder builder, int statusCode, Type? type = null, string contentType = "application/json")
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(type is null
            ? new ProducesResponseTypeMetadata(statusCode, typeof(void))
            : new ProducesResponseTypeMetadata(statusCode, type, [contentType]));

    private static TBuilder WithProblem<TBuilder>(this TBuilder builder, int statusCode)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithResponse(statusCode, typeof(ProblemDetails), "application/problem+json");

    /// <summary>Starts the durable work which an endpoint's handler returns as the operation which the request's key names.</summary>
    private static async ValueTask<object?> StartAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var operations = httpContext.RequestServices.GetRequiredService<DurableHttpOperations>();
        if (!DurableHttpRequestHeaders.TryGetIdempotencyKey(httpContext.Request.Headers, out var key, out var error))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Idempotency-Key is invalid", detail: error);
        }

        if (key is null && operations.Options.RequireIdempotencyKey)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Idempotency-Key is missing",
                detail: "This operation requires an Idempotency-Key header, which identifies the request when it is retried.");
        }

        var result = await next(context);
        if (result is not DurableTask work)
        {
            // The handler answered without durable work, such as with a validation problem.
            return result;
        }

        return await operations.StartAsync(httpContext, DurableOperationsMetadata.Get(httpContext), work, key ?? Guid.NewGuid().ToString("N"));
    }
}
