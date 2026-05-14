using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable value journal entries.
/// </summary>
public sealed class JsonDurableValueCommandCodec<T>(JsonSerializerOptions? options = null)
    : IDurableValueCommandCodec<T>
{
    private readonly JsonTypeInfo<T> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteSet(T value, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _valueTypeInfo, value),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Set);
                JsonSerializer.Serialize(jsonWriter, command.value, command.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableValueCommandHandler<T> consumer)
    {
        var reader = new JsonCommandReader(input);
        try
        {
            Apply(ref reader, consumer);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void Apply(ref JsonCommandReader reader, IDurableValueCommandHandler<T> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Set:
                consumer.ApplySet(reader.DeserializeAllowNull(1, JsonJournalEntryFields.Value, _valueTypeInfo)!);
                reader.EnsureEnd(2);
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
/// <summary>
/// JSON codec for durable persistent state journal entries.
/// </summary>
public sealed class JsonPersistentStateCommandCodec<T>(JsonSerializerOptions? options = null)
    : IPersistentStateCommandCodec<T>
{
    private readonly JsonTypeInfo<T> _stateTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _stateTypeInfo, state, version),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Set);
                JsonSerializer.Serialize(jsonWriter, command.state, command.typeInfo);
                jsonWriter.WriteNumberValue(command.version);
            });
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IPersistentStateCommandHandler<T> consumer)
    {
        var reader = new JsonCommandReader(input);
        try
        {
            Apply(ref reader, consumer);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void Apply(ref JsonCommandReader reader, IPersistentStateCommandHandler<T> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Set:
                consumer.ApplySet(
                    reader.DeserializeAllowNull(1, JsonJournalEntryFields.State, _stateTypeInfo)!,
                    reader.ReadUInt64(2, JsonJournalEntryFields.Version));
                reader.EnsureEnd(3);
                break;
            case JsonJournalEntryCommands.Clear:
                reader.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

}

/// <summary>
/// JSON codec for durable task completion source journal entries.
/// </summary>
public sealed class JsonDurableTaskCompletionSourceCommandCodec<T>(JsonSerializerOptions? options = null)
    : IDurableTaskCompletionSourceCommandCodec<T>
{
    private readonly JsonTypeInfo<T> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WritePending(JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            JsonJournalEntryCommands.Pending,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            (typeInfo: _valueTypeInfo, value),
            static (jsonWriter, command) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Completed);
                JsonSerializer.Serialize(jsonWriter, command.value, command.typeInfo);
            });
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            exception.Message,
            static (jsonWriter, message) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Faulted);
                jsonWriter.WriteStringValue(message);
            });
    }

    /// <inheritdoc/>
    public void WriteCanceled(JournalStreamWriter writer)
    {
        JsonCommandWriter.Write(
            writer,
            JsonJournalEntryCommands.Canceled,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
    }

    /// <inheritdoc/>
    public void Apply(JournalBufferReader input, IDurableTaskCompletionSourceCommandHandler<T> consumer)
    {
        var reader = new JsonCommandReader(input);
        try
        {
            Apply(ref reader, consumer);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void Apply(ref JsonCommandReader reader, IDurableTaskCompletionSourceCommandHandler<T> consumer)
    {
        var command = reader.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Pending:
                reader.EnsureEnd(1);
                consumer.ApplyPending();
                break;
            case JsonJournalEntryCommands.Completed:
                consumer.ApplyCompleted(reader.DeserializeAllowNull(1, JsonJournalEntryFields.Value, _valueTypeInfo)!);
                reader.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Faulted:
                consumer.ApplyFaulted(new Exception(reader.ReadString(1, JsonJournalEntryFields.Message)));
                reader.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Canceled:
                reader.EnsureEnd(1);
                consumer.ApplyCanceled();
                break;
            default:
                reader.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

}
