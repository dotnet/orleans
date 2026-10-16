using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Orleans.DurableTasks.Http;

/// <summary>Formats and reads Nexus operation tokens, which name an operation of a Nexus service.</summary>
/// <remarks>
/// A Nexus caller cancels an operation by its token, so the token carries the operation's identifier. Register an
/// implementation in the service collection to replace the default, <see cref="PlainTextNexusOperationTokenFormat"/>,
/// for example with one which signs or encrypts tokens.
/// </remarks>
public interface INexusOperationTokenFormat
{
    /// <summary>Returns the token of the operation <paramref name="operationId"/>.</summary>
    /// <param name="operationId">The operation identifier, which the caller chose with its <c>Nexus-Request-Id</c>.</param>
    /// <returns>A token of characters which are valid in an HTTP header value.</returns>
    string Format(string operationId);

    /// <summary>Reads a token which <see cref="Format"/> returned.</summary>
    /// <param name="token">The token.</param>
    /// <param name="operationId">The operation identifier.</param>
    /// <returns><see langword="true"/> when <paramref name="token"/> is a valid token.</returns>
    bool TryParse(string token, [NotNullWhen(true)] out string? operationId);
}

/// <summary>Formats a Nexus operation token as the operation's identifier itself.</summary>
public sealed class PlainTextNexusOperationTokenFormat : INexusOperationTokenFormat
{
    /// <inheritdoc />
    public string Format(string operationId)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        return operationId;
    }

    /// <inheritdoc />
    public bool TryParse(string token, [NotNullWhen(true)] out string? operationId)
    {
        ArgumentNullException.ThrowIfNull(token);
        operationId = DurableHttpRequestHeaders.IsValidOperationId(token) ? token : null;
        return operationId is not null;
    }
}
/// <summary>Names the HTTP headers and states of the Nexus protocol.</summary>
internal static class NexusProtocol
{
    public const string RequestId = "Nexus-Request-Id";
    public const string OperationToken = "Nexus-Operation-Token";
    public const string OperationState = "Nexus-Operation-State";
    public const string RequestTimeout = "Request-Timeout";
    public const string TokenQuery = "token";

    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public const string OperationErrorType = "nexus.OperationError";
    public const string HandlerErrorType = "nexus.HandlerError";

    /// <summary>Returns a Nexus <c>HandlerError</c> response of <paramref name="type"/>, such as <c>NOT_FOUND</c>.</summary>
    public static IResult HandlerError(int statusCode, string type, string message) => TypedResults.Json(
        new NexusFailure(message, new() { ["type"] = HandlerErrorType }, new() { ["type"] = type }),
        NexusJsonContext.Default.NexusFailure,
        statusCode: statusCode);
}

/// <summary>A Nexus <c>Failure</c>.</summary>
internal sealed record NexusFailure(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("metadata")] Dictionary<string, string> Metadata,
    [property: JsonPropertyName("details")] Dictionary<string, string> Details);

/// <summary>A Nexus <c>OperationInfo</c>.</summary>
internal sealed record NexusOperationInfo(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("state")] string State);

[JsonSerializable(typeof(NexusFailure))]
[JsonSerializable(typeof(NexusOperationInfo))]
internal sealed partial class NexusJsonContext : JsonSerializerContext;

/// <summary>Identifies the Nexus service of an endpoint.</summary>
/// <param name="Scope">The scope of the service's operations.</param>
internal sealed record NexusServiceMetadata(string Scope)
{
    /// <summary>Returns the Nexus service of the request's endpoint.</summary>
    public static NexusServiceMetadata Get(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<NexusServiceMetadata>()
        ?? throw new InvalidOperationException(
            $"Nexus operation endpoints belong to a group which MapNexusService maps, but '{context.GetEndpoint()?.DisplayName}' doesn't.");

    /// <summary>Returns the key of the operation which <paramref name="mappedId"/> maps for the request.</summary>
    public string GetKey(string mappedId) => TaskId.CreateRoot(Scope).Child(mappedId).ToString();
}
