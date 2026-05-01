using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable value log entries.
/// </summary>
public sealed class JsonValueOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableValueOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonValueSerializer<T> _valueSerializer = new(options);

    /// <inheritdoc/>
    public void WriteSet(T value, IBufferWriter<byte> output) => Write(output, CreateSetOperation(value));

    /// <inheritdoc/>
    public void WriteSet(T value, LogWriter writer) => Write(writer, CreateSetOperation(value));

    private JsonValueOperation CreateSetOperation(T value)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Set,
            Value = _valueSerializer.SerializeToElement(value)
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
                consumer.ApplySet(_valueSerializer.Deserialize(operation.Value.GetValueOrDefault())!);
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

    private static void Write(LogWriter writer, JsonValueOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonValueOperationConverter.WriteArrayElements(jsonWriter, operation),
            static (output, operation) => WriteBytes(output, operation));
    }

    private static void Write(IBufferWriter<byte> output, JsonValueOperation operation)
    {
        using var writer = new Utf8JsonWriter(output);
        WriteJson(writer, operation);
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonValueOperation operation)
    {
        JsonSerializer.Serialize(writer, operation, JsonOperationCodecsJsonContext.Default.JsonValueOperation);
    }

    private static void WriteBytes(IBufferWriter<byte> output, JsonValueOperation operation) => Write(output, operation);
}

/// <summary>
/// JSON codec for durable persistent state log entries.
/// </summary>
public sealed class JsonStateOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableStateOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonValueSerializer<T> _stateSerializer = new(options);

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, IBufferWriter<byte> output) => Write(output, CreateSetOperation(state, version));

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, LogWriter writer) => Write(writer, CreateSetOperation(state, version));

    private JsonStateOperation CreateSetOperation(T state, ulong version)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Set,
            State = _stateSerializer.SerializeToElement(state),
            Version = version
        };
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output) => Write(output, CreateClearOperation());

    /// <inheritdoc/>
    public void WriteClear(LogWriter writer) => Write(writer, CreateClearOperation());

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
                    _stateSerializer.Deserialize(operation.State.GetValueOrDefault())!,
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

    private static void Write(LogWriter writer, JsonStateOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonStateOperationConverter.WriteArrayElements(jsonWriter, operation),
            static (output, operation) => WriteBytes(output, operation));
    }

    private static void Write(IBufferWriter<byte> output, JsonStateOperation operation)
    {
        using var writer = new Utf8JsonWriter(output);
        WriteJson(writer, operation);
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonStateOperation operation)
    {
        JsonSerializer.Serialize(writer, operation, JsonOperationCodecsJsonContext.Default.JsonStateOperation);
    }

    private static void WriteBytes(IBufferWriter<byte> output, JsonStateOperation operation) => Write(output, operation);
}

/// <summary>
/// JSON codec for durable task completion source log entries.
/// </summary>
public sealed class JsonTcsOperationCodec<T>(JsonSerializerOptions? options = null)
    : IDurableTaskCompletionSourceOperationCodec<T>, IJsonLogEntryCodec
{
    private readonly JsonValueSerializer<T> _valueSerializer = new(options);

    /// <inheritdoc/>
    public void WritePending(IBufferWriter<byte> output) => Write(output, CreatePendingOperation());

    /// <inheritdoc/>
    public void WritePending(LogWriter writer) => Write(writer, CreatePendingOperation());

    private static JsonTaskCompletionSourceOperation CreatePendingOperation() => new() { Command = JsonLogEntryCommands.Pending };

    /// <inheritdoc/>
    public void WriteCompleted(T value, IBufferWriter<byte> output) => Write(output, CreateCompletedOperation(value));

    /// <inheritdoc/>
    public void WriteCompleted(T value, LogWriter writer) => Write(writer, CreateCompletedOperation(value));

    private JsonTaskCompletionSourceOperation CreateCompletedOperation(T value)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Completed,
            Value = _valueSerializer.SerializeToElement(value)
        };
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, IBufferWriter<byte> output) => Write(output, CreateFaultedOperation(exception));

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, LogWriter writer) => Write(writer, CreateFaultedOperation(exception));

    private static JsonTaskCompletionSourceOperation CreateFaultedOperation(Exception exception)
    {
        return new()
        {
            Command = JsonLogEntryCommands.Faulted,
            Message = exception.Message
        };
    }

    /// <inheritdoc/>
    public void WriteCanceled(IBufferWriter<byte> output) => Write(output, CreateCanceledOperation());

    /// <inheritdoc/>
    public void WriteCanceled(LogWriter writer) => Write(writer, CreateCanceledOperation());

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
                consumer.ApplyCompleted(_valueSerializer.Deserialize(operation.Value.GetValueOrDefault())!);
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

    private static void Write(LogWriter writer, JsonTaskCompletionSourceOperation operation)
    {
        JsonOperationCodecWriter.Write(
            writer,
            operation,
            static (jsonWriter, operation) => JsonTaskCompletionSourceOperationConverter.WriteArrayElements(jsonWriter, operation),
            static (output, operation) => WriteBytes(output, operation));
    }

    private static void Write(IBufferWriter<byte> output, JsonTaskCompletionSourceOperation operation)
    {
        using var writer = new Utf8JsonWriter(output);
        WriteJson(writer, operation);
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonTaskCompletionSourceOperation operation)
    {
        JsonSerializer.Serialize(writer, operation, JsonOperationCodecsJsonContext.Default.JsonTaskCompletionSourceOperation);
    }

    private static void WriteBytes(IBufferWriter<byte> output, JsonTaskCompletionSourceOperation operation) => Write(output, operation);
}
