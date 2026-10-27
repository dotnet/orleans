using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Orleans.DurableTasks.Http;

/// <summary>Configures endpoints whose handlers return durable work, which the endpoints start as operations.</summary>
internal static class DurableTaskEndpointConventions
{
    private static readonly string[] JsonContentTypes = ["application/json"];
    private static readonly string[] ProblemContentTypes = ["application/problem+json"];

    /// <summary>
    /// Replaces the response metadata which a durable start's handler infers, since the endpoint responds with the
    /// operation rather than the durable work, with the operation's responses.
    /// </summary>
    /// <exception cref="InvalidOperationException">The start answers only safe methods, such as <c>GET</c>.</exception>
    public static void DescribeStart(EndpointBuilder endpoint)
    {
        if (endpoint.Metadata.OfType<MethodInfo>().LastOrDefault() is not { } method || GetResultType(method) is not { } resultType)
        {
            return;
        }

        // A start creates an operation, which a safe method, whose requests clients and caches repeat freely, doesn't.
        if (endpoint.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault() is { HttpMethods: { Count: > 0 } methods }
            && methods.All(static method => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)))
        {
            throw new InvalidOperationException(
                $"'{endpoint.DisplayName}' returns durable work, which starts an operation, from a safe method. Map the start with POST, PUT, PATCH, or DELETE.");
        }

        for (var index = endpoint.Metadata.Count - 1; index >= 0; index--)
        {
            if (endpoint.Metadata[index] is IProducesResponseTypeMetadata { Type: { } type } && typeof(DurableTask).IsAssignableFrom(type))
            {
                endpoint.Metadata.RemoveAt(index);
            }
        }

        endpoint.Metadata.Add(resultType == typeof(void)
            ? new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void), [])
            : new ProducesResponseTypeMetadata(StatusCodes.Status200OK, resultType, JsonContentTypes));
        endpoint.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status202Accepted, typeof(DurableOperation), JsonContentTypes));
        endpoint.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status424FailedDependency, typeof(DurableOperation), JsonContentTypes));
        foreach (var status in (ReadOnlySpan<int>)[StatusCodes.Status400BadRequest, StatusCodes.Status422UnprocessableEntity, StatusCodes.Status503ServiceUnavailable])
        {
            endpoint.Metadata.Add(new ProducesResponseTypeMetadata(status, typeof(ProblemDetails), ProblemContentTypes));
        }
    }

    /// <summary>
    /// Returns the result type of a handler which returns durable work, <see cref="void"/> for work without a result, or
    /// <see langword="null"/> for a handler which doesn't return durable work.
    /// </summary>
    public static Type? GetResultType(MethodInfo handler)
    {
        var returnType = handler.ReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() is var definition
            && (definition == typeof(Task<>) || definition == typeof(ValueTask<>)))
        {
            returnType = returnType.GetGenericArguments()[0];
        }

        for (var type = returnType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DurableTask<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return typeof(DurableTask).IsAssignableFrom(returnType) ? typeof(void) : null;
    }

    /// <summary>Throws when <paramref name="name"/>, a name which is a URL path segment, has characters other than URL-unreserved ones.</summary>
    public static void ThrowIfInvalidName(string name, string? parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, parameter);
        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_' or '~'))
            {
                throw new ArgumentException($"A name in a route has URL-unreserved characters, but '{name}' has '{character}'.", parameter);
            }
        }
    }
}
