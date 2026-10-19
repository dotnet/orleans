using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Orleans.DurableTasks.Http;

/// <summary>Identifies the durable operations group of an endpoint, and derives the keys of its operations.</summary>
/// <param name="Scope">The scope of the group's operations.</param>
/// <param name="OperationEndpointName">The name of the group's status endpoint, which locates operations.</param>
internal sealed record DurableOperationsMetadata(string Scope, string OperationEndpointName)
{
    private RouteEndpoint? _operationEndpoint;

    /// <summary>Returns the durable operations group of the request's endpoint.</summary>
    public static DurableOperationsMetadata Get(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<DurableOperationsMetadata>()
        ?? throw new InvalidOperationException(
            $"Durable operation endpoints belong to a group which WithDurableOperations configures, but '{context.GetEndpoint()?.DisplayName}' doesn't.");

    /// <summary>Returns the group's status endpoint, whose route pattern includes the group's prefix.</summary>
    public RouteEndpoint GetOperationEndpoint(EndpointDataSource endpoints) => _operationEndpoint ??= endpoints.Endpoints
        .OfType<RouteEndpoint>()
        .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == OperationEndpointName);

    /// <summary>Returns the key of the operation <paramref name="operationId"/> which the request names.</summary>
    /// <remarks>
    /// The key is the group's scope, the request's values for the group's route parameters, the caller's scope, then the
    /// operation identifier, each a segment of its own. Route values scope operations to the resource which the group's
    /// prefix names, such as an order, and the caller's scope to the caller, so equal identifiers of two resources, or of
    /// two callers, name two operations.
    /// </remarks>
    public string GetKey(HttpContext context, EndpointDataSource endpoints, DurableHttpOptions options, string operationId)
    {
        var key = TaskId.CreateRoot(Scope);
        foreach (var parameter in GetOperationEndpoint(endpoints).RoutePattern.Parameters)
        {
            if (parameter.Name != DurableHttpOperations.OperationIdParameter)
            {
                key = key.Child(GetRouteValue(context, parameter.Name));
            }
        }

        return DurableOperationKeys.Complete(key, context, options, operationId);
    }

    private static string GetRouteValue(HttpContext context, string name) =>
        Convert.ToString(context.Request.RouteValues[name], CultureInfo.InvariantCulture) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"The request to '{context.GetEndpoint()?.DisplayName}' has no value for '{name}', a route parameter of its group's operations.");
}

/// <summary>Completes the keys of operations with their callers' scopes and identifiers.</summary>
internal static class DurableOperationKeys
{
    /// <summary>The prefix of the key segment which holds the caller's scope.</summary>
    private const char CallerPrefix = '@';

    /// <summary>
    /// Returns <paramref name="prefix"/> followed by the segment of the request's caller, from
    /// <see cref="DurableHttpOptions.GetCallerScope"/>, and the segment of <paramref name="operationId"/>.
    /// </summary>
    /// <remarks>
    /// The caller's segment is <c>@</c> for an anonymous caller, and <c>@{scope}</c> for a caller with a scope. Every key
    /// has exactly one caller segment, and <see cref="TaskId"/> escapes each segment, so distinct scopes and identifiers
    /// never share a key.
    /// </remarks>
    /// <exception cref="InvalidOperationException">An authenticated caller has no scope, or an empty one.</exception>
    public static string Complete(TaskId prefix, HttpContext context, DurableHttpOptions options, string operationId) =>
        prefix.Child(GetCallerSegment(context, options)).Child(operationId).ToString();

    private static string GetCallerSegment(HttpContext context, DurableHttpOptions options)
    {
        var scope = options.GetCallerScope(context);
        if (scope is null)
        {
            return context.User.Identity?.IsAuthenticated == true
                ? throw new InvalidOperationException(
                    $"The request to '{context.GetEndpoint()?.DisplayName}' has an authenticated caller without a scope, so its operations would be shared with anonymous callers. Configure DurableHttpOptions.GetCallerScope to return the scope of each authenticated caller.")
                : CallerPrefix.ToString();
        }

        return scope.Length > 0
            ? string.Concat(CallerPrefix.ToString(), scope)
            : throw new InvalidOperationException("DurableHttpOptions.GetCallerScope returned an empty scope. A caller's scope has at least one character.");
    }
}
