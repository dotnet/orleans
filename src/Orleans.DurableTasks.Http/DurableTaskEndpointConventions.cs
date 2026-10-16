using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
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
    public static void DescribeStart(EndpointBuilder endpoint)
    {
        if (endpoint.Metadata.OfType<MethodInfo>().LastOrDefault() is not { } method || GetResultType(method) is not { } resultType)
        {
            return;
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
        foreach (var status in (ReadOnlySpan<int>)[StatusCodes.Status400BadRequest, StatusCodes.Status422UnprocessableEntity, StatusCodes.Status502BadGateway])
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
}
