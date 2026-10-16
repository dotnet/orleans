using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Orleans.DurableTasks.Http;

/// <summary>Identifies the durable operations group of an endpoint, and derives the keys of its operations.</summary>
/// <param name="Scope">The scope of the group's operations, or <see langword="null"/> for the group's route prefix.</param>
/// <param name="OperationEndpointName">The name of the group's status endpoint, which locates operations.</param>
internal sealed record DurableOperationsMetadata(string? Scope, string OperationEndpointName)
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

    /// <summary>
    /// Returns the key of the operation whose identifier <see cref="DurableHttpOptions.MapOperationId"/> maps to
    /// <paramref name="mappedId"/> for the request.
    /// </summary>
    /// <remarks>
    /// The key of a group without a scope mirrors the path of its operations: the segments of the group's prefix, with the
    /// request's route values, then the mapped identifier, such as <c>orders/42/{id}</c>. The key of a group with a scope
    /// is the scope, the request's values for the group's route parameters, then the mapped identifier. Route values
    /// scope operations to the resource which the group's prefix names, such as an order, so equal identifiers of two
    /// resources name two operations.
    /// </remarks>
    public string GetKey(HttpContext context, EndpointDataSource endpoints, string mappedId)
    {
        var pattern = GetOperationEndpoint(endpoints).RoutePattern;
        var segments = new List<string>();
        if (Scope is not null)
        {
            segments.Add(Scope);
            foreach (var parameter in pattern.Parameters)
            {
                if (parameter.Name != DurableHttpOperations.OperationIdParameter)
                {
                    segments.Add(GetRouteValue(context, parameter.Name));
                }
            }
        }
        else
        {
            // The status endpoint's pattern is the group's prefix followed by 'operations/{operationId}'.
            var builder = new StringBuilder();
            foreach (var segment in pattern.PathSegments.SkipLast(2))
            {
                builder.Clear();
                foreach (var part in segment.Parts)
                {
                    builder.Append(part switch
                    {
                        RoutePatternLiteralPart literal => literal.Content,
                        RoutePatternSeparatorPart separator => separator.Content,
                        RoutePatternParameterPart parameter => GetRouteValue(context, parameter.Name),
                        _ => throw new UnreachableException(),
                    });
                }

                segments.Add(builder.ToString());
            }
        }

        segments.Add(mappedId);
        var key = TaskId.CreateRoot(segments[0]);
        foreach (var segment in segments.Skip(1))
        {
            key = key.Child(segment);
        }

        return key.ToString();
    }

    private static string GetRouteValue(HttpContext context, string name) =>
        Convert.ToString(context.Request.RouteValues[name], CultureInfo.InvariantCulture) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"The request to '{context.GetEndpoint()?.DisplayName}' has no value for '{name}', a route parameter of its group's operations.");
}
