using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps Nexus services, whose operations start durable work and cancel it, per the Nexus HTTP protocol.</summary>
/// <remarks>
/// <para>
/// Nexus is the protocol of Temporal's cross-namespace operations: a caller starts an operation with
/// <c>POST {endpoint}/{service}/{operation}</c> and cancels it with <c>POST {endpoint}/{service}/{operation}/cancel</c>,
/// addressing it by the token which the start returned. See
/// <see href="https://github.com/nexus-rpc/api/blob/main/SPEC.md">the Nexus HTTP specification</see>.
/// </para>
/// <para>
/// Nexus operations are durable operations like those which
/// <see cref="DurableHttpEndpointRouteBuilderExtensions.WithDurableOperations"/> configures: the caller's
/// <c>Nexus-Request-Id</c> names the operation, which <see cref="DurableHttpOptions.MapOperationId"/> maps within the
/// service's scope. A group of the same scope, without route parameters, addresses the same operations, so it can stream
/// their status or deliver their signals. Authentication and authorization belong to the application, which configures
/// them on the service's endpoints.
/// </para>
/// </remarks>
public static class NexusEndpointRouteBuilderExtensions
{
    // The handler binds its request like a Minimal API handler, which Minimal APIs analyze by reflection.
    private const string HandlerBindingUnreferencedCode =
        "This API may perform reflection on the supplied delegate and its parameters. These types may be trimmed if not directly referenced.";
    private const string HandlerBindingDynamicCode =
        "This API may perform reflection on the supplied delegate and its parameters. These types may require generated code and aren't compatible with native AOT applications.";

    /// <summary>Maps the Nexus service <paramref name="service"/>, whose operations <see cref="MapNexusOperation"/> maps.</summary>
    /// <param name="endpoints">The endpoint route builder, whose prefix is the Nexus endpoint, such as <c>/nexus</c>.</param>
    /// <param name="service">The service name, such as <c>payments.v1</c>, of URL-unreserved characters.</param>
    /// <param name="scope">
    /// The scope of the service's operations, or <see langword="null"/> for <c>nexus/{service}</c>.
    /// </param>
    /// <returns>A group builder which maps the service's operations and configures its endpoints.</returns>
    /// <exception cref="ArgumentException"><paramref name="service"/> is empty or has characters other than URL-unreserved ones.</exception>
    public static RouteGroupBuilder MapNexusService(this IEndpointRouteBuilder endpoints, string service, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ThrowIfInvalidName(service);
        return endpoints.MapGroup($"/{service}").WithMetadata(new NexusServiceMetadata(scope ?? $"nexus/{service}"));
    }

    /// <summary>
    /// Maps the Nexus operation <paramref name="operation"/>, whose handler returns durable work, such as
    /// <c>grain.ChargeAsync(input)</c>, which a start request starts as an operation.
    /// </summary>
    /// <param name="service">The group which <see cref="MapNexusService"/> returns.</param>
    /// <param name="operation">The operation name, such as <c>charge</c>, of URL-unreserved characters.</param>
    /// <param name="handler">
    /// A delegate which binds the operation's input, such as from the JSON request body, and other parameters like a Minimal
    /// API handler, and returns a durable grain call or a reference to an existing task, which
    /// <c>grain.GetDurableTask&lt;T&gt;(id)</c> returns, directly or through a <see cref="Task{TResult}"/> or
    /// <see cref="ValueTask{TResult}"/>. It runs for each start request, including retries, and only creates the work.
    /// </param>
    /// <returns>A group builder which configures the operation's start and cancel endpoints.</returns>
    /// <remarks>
    /// <para>
    /// <c>POST {service}/{operation}</c> starts the work as the operation which the request's <c>Nexus-Request-Id</c> names,
    /// or a new operation for a request without one, and responds:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>200 OK</c> with the result and <c>Nexus-Operation-State: succeeded</c> when the operation has succeeded, such as
    /// for a retry after it completed.
    /// </description></item>
    /// <item><description><c>201 Created</c> with an <c>OperationInfo</c>, whose token addresses the operation, while it runs.</description></item>
    /// <item><description>
    /// <c>424 Failed Dependency</c> with an <c>OperationError</c> failure, whose message
    /// <see cref="DurableHttpOptions.CreateError"/> describes, when the operation failed or was canceled.
    /// </description></item>
    /// <item><description>
    /// A handler error: <c>400</c> <c>BAD_REQUEST</c> for an invalid request identifier, <c>409</c> <c>CONFLICT</c> for a
    /// request identifier which another request used to start other work, and <c>503</c> <c>UNAVAILABLE</c> when the
    /// operation's host doesn't accept it, which a caller retries.
    /// </description></item>
    /// </list>
    /// <para>
    /// <c>POST {service}/{operation}/cancel</c> durably requests cancellation of the operation which its
    /// <c>Nexus-Operation-Token</c> header or <c>token</c> query value names and returns <c>202 Accepted</c>, including for
    /// a terminal operation, which is unchanged. A token which names no operation receives a <c>404</c> <c>NOT_FOUND</c>
    /// handler error. <see cref="INexusOperationTokenFormat"/> formats tokens.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="operation"/> is empty or has characters other than URL-unreserved ones, or <paramref name="handler"/>
    /// doesn't return durable work.
    /// </exception>
    [RequiresUnreferencedCode(HandlerBindingUnreferencedCode)]
    [RequiresDynamicCode(HandlerBindingDynamicCode)]
    public static RouteGroupBuilder MapNexusOperation(this IEndpointRouteBuilder service, string operation, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfInvalidName(operation);
        var resultType = DurableTaskEndpointConventions.GetResultType(handler.Method)
            ?? throw new ArgumentException(
                $"A Nexus operation's handler returns DurableTask or DurableTask<TResult>, not '{handler.Method.ReturnType}'.",
                nameof(handler));

        var group = service.MapGroup($"/{operation}");
        var start = group.MapPost("/", handler)
            .AddEndpointFilter(static async (context, next) =>
            {
                var httpContext = context.HttpContext;
                return await next(context) is DurableTask work
                    ? await httpContext.RequestServices.GetRequiredService<DurableNexusOperations>().StartAsync(httpContext, work)
                    : throw new InvalidOperationException($"The handler of '{httpContext.GetEndpoint()?.DisplayName}' returned no durable work.");
            });
        start.Add(static endpoint =>
        {
            for (var index = endpoint.Metadata.Count - 1; index >= 0; index--)
            {
                if (endpoint.Metadata[index] is IProducesResponseTypeMetadata { Type: { } type } && typeof(DurableTask).IsAssignableFrom(type))
                {
                    endpoint.Metadata.RemoveAt(index);
                }
            }
        });

        if (resultType == typeof(void))
        {
            start.Produces(StatusCodes.Status200OK);
        }
        else
        {
            start.Produces(StatusCodes.Status200OK, resultType);
        }

        start.Produces(StatusCodes.Status201Created, typeof(void), "application/json")
            .Produces(StatusCodes.Status424FailedDependency, typeof(void), "application/json");
        group.MapPost(
            "/cancel",
            static context => DurableHttpEndpointRouteBuilderExtensions.ExecuteAsync(
                context,
                context.RequestServices.GetRequiredService<DurableNexusOperations>().CancelAsync(context)));
        return group;
    }

    private static void ThrowIfInvalidName(string name, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(name))] string? parameter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, parameter);
        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_' or '~'))
            {
                throw new ArgumentException($"A Nexus name has URL-unreserved characters, but '{name}' has '{character}'.", parameter);
            }
        }
    }
}
