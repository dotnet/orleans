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
    public void WriteSet(T value, LogStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new SetOperation(_valueTypeInfo, value),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IDurableValueOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(operation.Deserialize(1, JsonLogEntryFields.Value, _valueTypeInfo)!);
                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableValueOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(ref reader, consumer);
    }

    private readonly struct SetOperation(JsonTypeInfo<T> typeInfo, T value)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(JsonLogEntryCommands.Set);
            JsonSerializer.Serialize(writer, value, typeInfo);
        }
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
    public void WriteSet(T state, ulong version, LogStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new SetOperation(_stateTypeInfo, state, version),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void WriteClear(LogStreamWriter writer) => WriteCommand(writer, JsonLogEntryCommands.Clear);

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableStateOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IDurableStateOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(
                    operation.Deserialize(1, JsonLogEntryFields.State, _stateTypeInfo)!,
                    operation.ReadUInt64(2, JsonLogEntryFields.Version));
                operation.EnsureEnd(3);
                break;
            case JsonLogEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableStateOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(ref reader, consumer);
    }

    private static void WriteCommand(LogStreamWriter writer, string command)
    {
        JsonOperationCodecWriter.Write(
            writer,
            command,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    private readonly struct SetOperation(JsonTypeInfo<T> typeInfo, T state, ulong version)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(JsonLogEntryCommands.Set);
            JsonSerializer.Serialize(writer, state, typeInfo);
            writer.WriteNumberValue(version);
        }
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
    public void WritePending(LogStreamWriter writer) => WriteCommand(writer, JsonLogEntryCommands.Pending);

    /// <inheritdoc/>
    public void WriteCompleted(T value, LogStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            new CompletedOperation(_valueTypeInfo, value),
            static (jsonWriter, operation) => operation.Write(jsonWriter));
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, LogStreamWriter writer)
    {
        JsonOperationCodecWriter.Write(
            writer,
            exception.Message,
            static (jsonWriter, message) =>
            {
                jsonWriter.WriteStringValue(JsonLogEntryCommands.Faulted);
                jsonWriter.WriteStringValue(message);
            });
    }

    /// <inheritdoc/>
    public void WriteCanceled(LogStreamWriter writer) => WriteCommand(writer, JsonLogEntryCommands.Canceled);

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonLogEntryCommands.Pending:
                operation.EnsureEnd(1);
                consumer.ApplyPending();
                break;
            case JsonLogEntryCommands.Completed:
                consumer.ApplyCompleted(operation.Deserialize(1, JsonLogEntryFields.Value, _valueTypeInfo)!);
                operation.EnsureEnd(2);
                break;
            case JsonLogEntryCommands.Faulted:
                consumer.ApplyFaulted(new Exception(operation.ReadString(1, JsonLogEntryFields.Message)));
                operation.EnsureEnd(2);
                break;
            case JsonLogEntryCommands.Canceled:
                operation.EnsureEnd(1);
                consumer.ApplyCanceled();
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

    void IJsonLogEntryCodec.Apply(ref JsonOperationReader reader, IDurableStateMachine stateMachine)
    {
        if (stateMachine is not IDurableTaskCompletionSourceOperationHandler<T> consumer)
        {
            throw new InvalidOperationException(
                $"State machine '{stateMachine.GetType().FullName}' is not compatible with codec '{GetType().FullName}'.");
        }

        Apply(ref reader, consumer);
    }

    private static void WriteCommand(LogStreamWriter writer, string command)
    {
        JsonOperationCodecWriter.Write(
            writer,
            command,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    private readonly struct CompletedOperation(JsonTypeInfo<T> typeInfo, T value)
    {
        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(JsonLogEntryCommands.Completed);
            JsonSerializer.Serialize(writer, value, typeInfo);
        }
    }
}
