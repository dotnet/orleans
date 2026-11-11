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
    [property: JsonPropertyName("message")] string Message)
{
    /// <summary>
    /// The code of a failed operation whose work never ran, because the grain which runs it refused it without storing
    /// it, such as when the work's identifier already names another method's task there. A client starts the work again
    /// with a new <c>Idempotency-Key</c>.
    /// </summary>
    public const string NotStartedCode = "NotStarted";

    /// <summary>
    /// The code of a failed operation whose work's grain didn't confirm the work, so whether it ran is unknown: the
    /// delivery of its start retired without an acknowledgement, or arrived after the grain's admission window, by which
    /// time the grain may have run it and deleted it. A client checks the work's effects, such as the resource it
    /// creates, before it starts the work again, so the work's side effects aren't duplicated.
    /// </summary>
    public const string OutcomeUnknownCode = "OutcomeUnknown";
}

/// <summary>Represents an operation whose host failed to start its work on the work's grain.</summary>
/// <remarks>
/// The operation's host fails with this exception when scheduling its work fails, so clients distinguish work which
/// never ran, <see cref="DurableOperationError.NotStartedCode"/>, and work whose outcome is unknown,
/// <see cref="DurableOperationError.OutcomeUnknownCode"/>, from work which ran and failed.
/// </remarks>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.Http.DurableOperationStartException")]
internal sealed class DurableOperationStartException : Exception
{
    private DurableOperationStartException()
    {
        ErrorCode = DurableOperationError.OutcomeUnknownCode;
    }

    private DurableOperationStartException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the operation's error code: <see cref="DurableOperationError.NotStartedCode"/> or
    /// <see cref="DurableOperationError.OutcomeUnknownCode"/>.
    /// </summary>
    [Id(0)]
    public string ErrorCode { get; private set; }

    /// <summary>
    /// Returns the exception which reports <paramref name="failure"/>, a failure to schedule the operation's work, or
    /// <see langword="null"/> for another failure.
    /// </summary>
    /// <remarks>
    /// Only a refusal which the work's grain makes without storing the work, a conflict or a rejection with its own code,
    /// proves that the work never ran. A delivery which retired unacknowledged, or an invocation which arrived after the
    /// grain's admission window, leaves the outcome unknown: the grain may have run the work, and even deleted it.
    /// </remarks>
    public static DurableOperationStartException? FromScheduleFailure(Exception failure) => failure switch
    {
        DurableTaskConflictException => NotStarted(failure),
        DurableTaskRejectedException { Code: DurableTaskRejectionCodes.Expired } => OutcomeUnknown(failure),
        DurableTaskRejectedException => NotStarted(failure),
        DurableTaskDeliveryException => OutcomeUnknown(failure),
        _ => null,
    };

    private static DurableOperationStartException NotStarted(Exception failure) =>
        new(DurableOperationError.NotStartedCode, "The operation's work wasn't started.", failure);

    private static DurableOperationStartException OutcomeUnknown(Exception failure) =>
        new(DurableOperationError.OutcomeUnknownCode, "The operation's work wasn't confirmed, so whether it ran is unknown.", failure);
}
/// <summary>Describes durable task responses as operations, for every protocol which serves them.</summary>
internal static class DurableOperationResponses
{
    /// <summary>Returns the error which describes a failed or canceled response to clients.</summary>
    public static DurableOperationError GetError(DurableTaskResponse response, DurableHttpOptions options) => response.Exception switch
    {
        DurableOperationStartException start => new(start.ErrorCode, start.Message),
        { } exception => options.CreateError(exception),
        null => throw new InvalidOperationException($"A {response.Status} response carries its exception."),
    };

    /// <summary>Returns the status monitor of the operation <paramref name="operationId"/>, whose response is <paramref name="response"/>.</summary>
    public static DurableOperation ToOperation(string operationId, DurableTaskResponse response, JsonSerializerOptions json, DurableHttpOptions options) =>
        response.Status switch
        {
            DurableTaskStatus.CompletedSuccessfully => new(
                operationId,
                DurableOperationStatus.Succeeded,
                Result: response.ResultType is { } resultType ? JsonSerializer.SerializeToElement(response.Result, json.GetTypeInfo(resultType)) : null),
            DurableTaskStatus.Failed => new(operationId, DurableOperationStatus.Failed, Error: GetError(response, options)),
            DurableTaskStatus.Canceled => new(operationId, DurableOperationStatus.Canceled, Error: GetError(response, options)),
            _ => new(operationId, DurableOperationStatus.Running),
        };

    /// <summary>Returns the Nexus state of a terminal response.</summary>
    public static string GetNexusState(DurableTaskResponse response) => response.Status switch
    {
        DurableTaskStatus.CompletedSuccessfully => NexusProtocol.Succeeded,
        DurableTaskStatus.Failed => NexusProtocol.Failed,
        DurableTaskStatus.Canceled => NexusProtocol.Canceled,
        var status => throw new InvalidOperationException($"A Nexus state describes a terminal response, not a {status} one."),
    };

    /// <summary>Returns the Nexus <c>Failure</c> which reports a failed or canceled response.</summary>
    public static NexusFailure ToNexusFailure(DurableTaskResponse response, DurableHttpOptions options)
    {
        var error = GetError(response, options);
        return new(
            error.Message,
            new() { ["type"] = NexusProtocol.OperationErrorType },
            new() { ["state"] = GetNexusState(response), ["code"] = error.Code });
    }
}

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
