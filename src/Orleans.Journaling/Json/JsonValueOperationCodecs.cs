using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable value log entries.
/// </summary>
public sealed class JsonValueOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableValueOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonTypeInfo<T> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteSet(T value, LogStreamWriter writer) => Write(writer, CreateSetOperation(value));

    private JsonValueOperation CreateSetOperation(T value)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Set,
            Value = JsonSerializer.SerializeToElement(value, _valueTypeInfo)
        };
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        var operation = JsonSerializer.Deserialize(ref reader, JsonOperationCodecsJsonContext.Default.JsonValueOperation);
        Apply(operation, consumer);
    }

    internal void Apply(JsonElement root, IDurableValueOperationHandler<T> consumer)
    {
        Apply(root.Deserialize(JsonOperationCodecsJsonContext.Default.JsonValueOperation), consumer);
    }

    private void Apply(JsonValueOperation operation, IDurableValueOperationHandler<T> consumer)
    {
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(operation.Value.GetValueOrDefault().Deserialize(_valueTypeInfo)!);
                break;
            default:
                throw new NotSupportedException($"Command type '{operation.Command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(JsonElement entry, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableValueOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(entry, consumer);
    }

    private static void Write(LogStreamWriter writer, JsonValueOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonValueOperationConverter.WriteArrayElements(jsonWriter, operation));
    }
}

/// <summary>
/// JSON codec for durable persistent state log entries.
/// </summary>
public sealed class JsonStateOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableStateOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonTypeInfo<T> _stateTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, LogStreamWriter writer) => Write(writer, CreateSetOperation(state, version));

    private JsonStateOperation CreateSetOperation(T state, ulong version)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Set,
            State = JsonSerializer.SerializeToElement(state, _stateTypeInfo),
            Version = version
        };
    }

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) => Write(writer, CreateClearOperation());

    private static JsonStateOperation CreateClearOperation() => new() { Command = JsonLogEntryCommands.Clear };

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableStateOperationHandler<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        var operation = JsonSerializer.Deserialize(ref reader, JsonOperationCodecsJsonContext.Default.JsonStateOperation);
        Apply(operation, consumer);
    }

    internal void Apply(JsonElement root, IDurableStateOperationHandler<T> consumer)
    {
        Apply(root.Deserialize(JsonOperationCodecsJsonContext.Default.JsonStateOperation), consumer);
    }

    private void Apply(JsonStateOperation operation, IDurableStateOperationHandler<T> consumer)
    {
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(
                    operation.State.GetValueOrDefault().Deserialize(_stateTypeInfo)!,
                    operation.Version.GetValueOrDefault());
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            default:
                throw new NotSupportedException($"Command type '{operation.Command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(JsonElement entry, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableStateOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(entry, consumer);
    }

    private static void Write(LogStreamWriter writer, JsonStateOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonStateOperationConverter.WriteArrayElements(jsonWriter, operation));
    }
}

/// <summary>
/// JSON codec for durable task completion source log entries.
/// </summary>
public sealed class JsonTcsOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableTaskCompletionSourceOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonTypeInfo<T> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WritePending(LogStreamWriter writer) => Write(writer, CreatePendingOperation());

    private static JsonTaskCompletionSourceOperation CreatePendingOperation() => new() { Command = JsonLogEntryCommands.Pending };

    /// <inheritdoc/>
    public void WriteCompleted(T value, LogStreamWriter writer) => Write(writer, CreateCompletedOperation(value));

    private JsonTaskCompletionSourceOperation CreateCompletedOperation(T value)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Completed,
            Value = JsonSerializer.SerializeToElement(value, _valueTypeInfo)
        };
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, LogStreamWriter writer) => Write(writer, CreateFaultedOperation(exception));

    private static JsonTaskCompletionSourceOperation CreateFaultedOperation(Exception exception)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Faulted,
            Message = exception.Message
        };
    }

    /// <inheritdoc/>
    public void WriteCanceled(LogStreamWriter writer) => Write(writer, CreateCanceledOperation());

    private static JsonTaskCompletionSourceOperation CreateCanceledOperation() => new() { Command = JsonLogEntryCommands.Canceled };

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        var operation = JsonSerializer.Deserialize(ref reader, JsonOperationCodecsJsonContext.Default.JsonTaskCompletionSourceOperation);
        Apply(operation, consumer);
    }

    internal void Apply(JsonElement root, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        Apply(root.Deserialize(JsonOperationCodecsJsonContext.Default.JsonTaskCompletionSourceOperation), consumer);
    }

    private void Apply(JsonTaskCompletionSourceOperation operation, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        switch (operation.Command)
        {
            case JsonLogEntryCommands.Pending:
                consumer.ApplyPending();
                break;
            case JsonLogEntryCommands.Completed:
                consumer.ApplyCompleted(operation.Value.GetValueOrDefault().Deserialize(_valueTypeInfo)!);
                break;
            case JsonLogEntryCommands.Faulted:
                consumer.ApplyFaulted(new Exception(operation.Message));
                break;
            case JsonLogEntryCommands.Canceled:
                consumer.ApplyCanceled();
                break;
            default:
                throw new NotSupportedException($"Command type '{operation.Command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(JsonElement entry, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableTaskCompletionSourceOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(entry, consumer);
    }

    private static void Write(LogStreamWriter writer, JsonTaskCompletionSourceOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonTaskCompletionSourceOperationConverter.WriteArrayElements(jsonWriter, operation));
    }
}
