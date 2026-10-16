using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.DurableTasks.Http;

/// <summary>
/// Represents an operation's status monitor: the resource which reports a durable task's status and, once it is
/// terminal, its result or error.
/// </summary>
/// <param name="Id">The operation identifier, which addresses the status monitor.</param>
/// <param name="Status">The operation's status.</param>
/// <param name="Result">The task's result, when it succeeded with a value.</param>
/// <param name="Error">The task's failure or cancellation, when it failed or was canceled.</param>
/// <remarks>
/// The document has the shape of an Azure long-running operation status monitor, so clients which poll Azure operations
/// read it as well. Durable tasks record it as the result of <see cref="IDurableHttpClient"/> steps.
/// </remarks>
[Alias("Orleans.DurableTasks.Http.DurableOperation")]
public sealed record DurableOperation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] DurableOperationStatus Status,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DurableOperationError? Error = null)
{
    /// <summary>Gets whether the operation has reached a terminal status.</summary>
    [JsonIgnore]
    public bool IsTerminal => Status is DurableOperationStatus.Succeeded or DurableOperationStatus.Failed or DurableOperationStatus.Canceled;

    /// <summary>
    /// Reads a status monitor which a server returned, as Azure clients do: a <c>status</c> which this package's statuses
    /// don't name, such as Azure Resource Manager's <c>InProgress</c> or <c>Accepted</c>, reads as
    /// <see cref="DurableOperationStatus.Running"/>.
    /// </summary>
    internal static DurableOperation Read(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A status monitor is a JSON object.");
        }

        var status = ReadString(root, "status") is { } value && Enum.TryParse<DurableOperationStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : DurableOperationStatus.Running;
        var result = root.TryGetProperty("result", out var element) && element.ValueKind != JsonValueKind.Null ? element.Clone() : (JsonElement?)null;
        var error = root.TryGetProperty("error", out element) && element.ValueKind == JsonValueKind.Object
            ? new DurableOperationError(ReadString(element, "code") ?? string.Empty, ReadString(element, "message") ?? string.Empty)
            : null;
        return new(ReadString(root, "id") ?? string.Empty, status, result, error);

        static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>Serializes a <see cref="DurableOperation"/> for Orleans, with its result as JSON text.</summary>
[GenerateSerializer, Immutable]
internal readonly record struct DurableOperationSurrogate(
    string Id,
    DurableOperationStatus Status,
    string? Result,
    DurableOperationError? Error);

[RegisterConverter]
internal sealed class DurableOperationSurrogateConverter : IConverter<DurableOperation, DurableOperationSurrogate>
{
    public DurableOperation ConvertFromSurrogate(in DurableOperationSurrogate surrogate) => new(
        surrogate.Id,
        surrogate.Status,
        surrogate.Result is { } result ? ParseResult(result) : null,
        surrogate.Error);

    private static JsonElement ParseResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public DurableOperationSurrogate ConvertToSurrogate(in DurableOperation value) =>
        new(value.Id, value.Status, value.Result?.GetRawText(), value.Error);
}

/// <summary>Identifies the status of an operation.</summary>
/// <remarks>The values are Azure's long-running operation statuses, serialized by name.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DurableOperationStatus>))]
[GenerateSerializer]
[Alias("Orleans.DurableTasks.Http.DurableOperationStatus")]
public enum DurableOperationStatus
{
    /// <summary>The operation has not started.</summary>
    NotStarted,

    /// <summary>The operation is running. A durable task reports this status until it is terminal.</summary>
    Running,

    /// <summary>The operation completed successfully.</summary>
    Succeeded,

    /// <summary>The operation failed.</summary>
    Failed,

    /// <summary>The operation was canceled.</summary>
    Canceled,
}

/// <summary>Describes why an operation failed or was canceled.</summary>
/// <param name="Code">A machine-readable code, which <see cref="DurableHttpOptions.CreateError"/> chooses.</param>
/// <param name="Message">A human-readable description.</param>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Http.DurableOperationError")]
public sealed record DurableOperationError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Requests cancellation of an operation.</summary>
/// <param name="Reason">
/// The reason which the canceled task reads from <see cref="DurableTaskContext.TaskCancellationReason"/>, or
/// <see langword="null"/> for none.
/// </param>
public sealed record DurableOperationCancelRequest(
    [property: JsonPropertyName("reason")] string? Reason);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DurableOperation))]
[JsonSerializable(typeof(DurableOperationCancelRequest))]
internal sealed partial class DurableHttpJsonContext : JsonSerializerContext;

internal static class DurableHttpJsonExtensions
{
    /// <summary>
    /// Returns the contract for <typeparamref name="T"/> which <paramref name="options"/> resolve, so serialization uses the
    /// options' resolver, such as an application's source-generated context, rather than reflection.
    /// </summary>
    public static JsonTypeInfo<T> GetTypeInfo<T>(this JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
