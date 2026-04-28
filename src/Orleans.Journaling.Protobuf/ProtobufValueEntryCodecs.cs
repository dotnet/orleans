using System.Buffers;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers codec for durable value log entries.
/// </summary>
public sealed class ProtobufValueEntryCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableValueCodec<T>
{
    private const uint CommandField = 1;
    private const uint ValueField = 2;
    private const uint SetCommand = 0;

    /// <inheritdoc/>
    public void WriteSet(T value, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, SetCommand);
        converter.WriteField(output, ValueField, value);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableValueLogEntryConsumer<T> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        var command = uint.MaxValue;
        var hasCommand = false;
        var hasValue = false;
        T? value = default;

        while (!reader.End)
        {
            var tag = ProtobufWire.ReadTag(ref reader);
            var field = tag >> 3;
            switch (field)
            {
                case CommandField:
                    ProtobufWire.RequireNoDuplicateCommand(hasCommand);
                    command = ProtobufWire.ReadUInt32(ref reader);
                    hasCommand = true;
                    break;
                case ValueField:
                    ProtobufWire.RequireCommand(hasCommand);
                    value = converter.FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasValue = true;
                    break;
                default:
                    ProtobufWire.SkipField(ref reader, tag);
                    break;
            }
        }

        ProtobufWire.RequireCommand(hasCommand);
        switch (command)
        {
            case SetCommand:
                consumer.ApplySet(ProtobufWire.RequireValue(hasValue, value, "value", command));
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}

/// <summary>
/// Protocol Buffers codec for durable persistent state log entries.
/// </summary>
public sealed class ProtobufStateEntryCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableStateCodec<T>
{
    private const uint CommandField = 1;
    private const uint StateField = 2;
    private const uint VersionField = 3;

    private const uint SetCommand = 0;
    private const uint ClearCommand = 1;

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, SetCommand);
        converter.WriteField(output, StateField, state);
        ProtobufWire.WriteUInt64Field(output, VersionField, version);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, ClearCommand);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableStateLogEntryConsumer<T> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        var command = uint.MaxValue;
        var version = 0UL;
        var hasCommand = false;
        var hasState = false;
        var hasVersion = false;
        T? state = default;

        while (!reader.End)
        {
            var tag = ProtobufWire.ReadTag(ref reader);
            var field = tag >> 3;
            switch (field)
            {
                case CommandField:
                    ProtobufWire.RequireNoDuplicateCommand(hasCommand);
                    command = ProtobufWire.ReadUInt32(ref reader);
                    hasCommand = true;
                    break;
                case StateField:
                    ProtobufWire.RequireCommand(hasCommand);
                    state = converter.FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasState = true;
                    break;
                case VersionField:
                    ProtobufWire.RequireCommand(hasCommand);
                    version = ProtobufWire.ReadUInt64(ref reader);
                    hasVersion = true;
                    break;
                default:
                    ProtobufWire.SkipField(ref reader, tag);
                    break;
            }
        }

        ProtobufWire.RequireCommand(hasCommand);
        switch (command)
        {
            case SetCommand:
                ProtobufWire.RequireField(hasVersion, "version", command);
                consumer.ApplySet(ProtobufWire.RequireValue(hasState, state, "state", command), version);
                break;
            case ClearCommand:
                consumer.ApplyClear();
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}

/// <summary>
/// Protocol Buffers codec for durable task completion source log entries.
/// </summary>
public sealed class ProtobufTcsEntryCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableTaskCompletionSourceCodec<T>
{
    private const uint CommandField = 1;
    private const uint ValueField = 2;
    private const uint MessageField = 3;

    private const uint PendingCommand = 0;
    private const uint CompletedCommand = 1;
    private const uint FaultedCommand = 2;
    private const uint CanceledCommand = 3;

    /// <inheritdoc/>
    public void WritePending(IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, PendingCommand);
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, CompletedCommand);
        converter.WriteField(output, ValueField, value);
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, FaultedCommand);
        ProtobufWire.WriteStringField(output, MessageField, exception.Message);
    }

    /// <inheritdoc/>
    public void WriteCanceled(IBufferWriter<byte> output)
    {
        ProtobufWire.WriteUInt32Field(output, CommandField, CanceledCommand);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceLogEntryConsumer<T> consumer)
    {
        var reader = new SequenceReader<byte>(input);
        var command = uint.MaxValue;
        var hasCommand = false;
        var hasValue = false;
        var hasMessage = false;
        T? value = default;
        string? message = null;

        while (!reader.End)
        {
            var tag = ProtobufWire.ReadTag(ref reader);
            var field = tag >> 3;
            switch (field)
            {
                case CommandField:
                    ProtobufWire.RequireNoDuplicateCommand(hasCommand);
                    command = ProtobufWire.ReadUInt32(ref reader);
                    hasCommand = true;
                    break;
                case ValueField:
                    ProtobufWire.RequireCommand(hasCommand);
                    value = converter.FromBytes(ProtobufWire.ReadBytes(ref reader));
                    hasValue = true;
                    break;
                case MessageField:
                    ProtobufWire.RequireCommand(hasCommand);
                    message = ProtobufWire.ReadString(ref reader);
                    hasMessage = true;
                    break;
                default:
                    ProtobufWire.SkipField(ref reader, tag);
                    break;
            }
        }

        ProtobufWire.RequireCommand(hasCommand);
        switch (command)
        {
            case PendingCommand:
                consumer.ApplyPending();
                break;
            case CompletedCommand:
                consumer.ApplyCompleted(ProtobufWire.RequireValue(hasValue, value, "value", command));
                break;
            case FaultedCommand:
                ProtobufWire.RequireField(hasMessage, "message", command);
                consumer.ApplyFaulted(new Exception(message));
                break;
            case CanceledCommand:
                consumer.ApplyCanceled();
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}
