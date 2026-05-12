using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable value journal entries.
/// </summary>
public sealed class JsonValueOperationCodec<T>(JsonSerializerOptions? options = null)
    : IValueOperationCodec<T>
{
    private readonly JsonTypeInfo<T> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteSet(T value, JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            (typeInfo: _valueTypeInfo, value),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Set);
                JsonSerializer.Serialize(jsonWriter, operation.value, operation.typeInfo);
            });
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IValueOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IValueOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Set:
                consumer.ApplySet(operation.DeserializeRequired(1, JsonJournalEntryFields.Value, _valueTypeInfo));
                operation.EnsureEnd(2);
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}

/// <summary>
/// JSON codec for durable persistent state journal entries.
/// </summary>
public sealed class JsonStateOperationCodec<T>(JsonSerializerOptions? options = null)
    : IStateOperationCodec<T>
{
    private readonly JsonTypeInfo<T> _stateTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            (typeInfo: _stateTypeInfo, state, version),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Set);
                JsonSerializer.Serialize(jsonWriter, operation.state, operation.typeInfo);
                jsonWriter.WriteNumberValue(operation.version);
            });
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteClear(JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            JsonJournalEntryCommands.Clear,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, IStateOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, IStateOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Set:
                consumer.ApplySet(
                    operation.DeserializeRequired(1, JsonJournalEntryFields.State, _stateTypeInfo),
                    operation.ReadUInt64(2, JsonJournalEntryFields.Version));
                operation.EnsureEnd(3);
                break;
            case JsonJournalEntryCommands.Clear:
                operation.EnsureEnd(1);
                consumer.ApplyClear();
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

}

/// <summary>
/// JSON codec for durable task completion source journal entries.
/// </summary>
public sealed class JsonTcsOperationCodec<T>(JsonSerializerOptions? options = null)
    : ITaskCompletionSourceOperationCodec<T>
{
    private readonly JsonTypeInfo<T> _valueTypeInfo = JsonTypeInfoHelpers.GetTypeInfo<T>(options);

    /// <inheritdoc/>
    public void WritePending(JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            JsonJournalEntryCommands.Pending,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            (typeInfo: _valueTypeInfo, value),
            static (jsonWriter, operation) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Completed);
                JsonSerializer.Serialize(jsonWriter, operation.value, operation.typeInfo);
            });
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            exception.Message,
            static (jsonWriter, message) =>
            {
                jsonWriter.WriteStringValue(JsonJournalEntryCommands.Faulted);
                jsonWriter.WriteStringValue(message);
            });
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void WriteCanceled(JournalStreamWriter writer)
    {
        var formattedEntry = JsonFormattedJournalEntry.Create(
            JsonJournalEntryCommands.Canceled,
            static (jsonWriter, command) => jsonWriter.WriteStringValue(command));
        if (writer.TryAppendFormattedEntry(formattedEntry))
        {
            return;
        }

        using var entry = writer.BeginEntry();
        using (var jsonWriter = new Utf8JsonWriter(entry.Writer))
        {
            formattedEntry.WriteTo(jsonWriter);
            jsonWriter.Flush();
        }

        entry.Commit();
    }

    /// <inheritdoc/>
    public void Apply(JournalReadBuffer input, ITaskCompletionSourceOperationHandler<T> consumer)
    {
        var operation = new JsonOperationReader(input);
        Apply(ref operation, consumer);
    }

    private void Apply(ref JsonOperationReader operation, ITaskCompletionSourceOperationHandler<T> consumer)
    {
        var command = operation.Command;
        switch (command)
        {
            case JsonJournalEntryCommands.Pending:
                operation.EnsureEnd(1);
                consumer.ApplyPending();
                break;
            case JsonJournalEntryCommands.Completed:
                consumer.ApplyCompleted(operation.DeserializeRequired(1, JsonJournalEntryFields.Value, _valueTypeInfo));
                operation.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Faulted:
                consumer.ApplyFaulted(new Exception(operation.ReadString(1, JsonJournalEntryFields.Message)));
                operation.EnsureEnd(2);
                break;
            case JsonJournalEntryCommands.Canceled:
                operation.EnsureEnd(1);
                consumer.ApplyCanceled();
                break;
            default:
                operation.EnsureEnd(1);
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }

}
