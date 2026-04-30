using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers codec for durable value log entries.
/// </summary>
public sealed class ProtobufValueOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableValueOperationCodec<T>
{
    private const uint SetCommand = 0;

    /// <inheritdoc/>
    public void WriteSet(T value, IBufferWriter<byte> output)
    {
        var operation = new ProtobufValueOperation();
        operation.Command.Add(SetCommand);
        operation.Value.Add(converter.ToByteString(value));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer)
    {
        var operation = ProtobufGeneratedCodecHelpers.Parse(input, ProtobufValueOperation.Parser, "value operation");
        var command = ProtobufGeneratedCodecHelpers.RequireCommand(operation.Command);
        switch (command)
        {
            case SetCommand:
                consumer.ApplySet(converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Value, "value", command)));
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}

/// <summary>
/// Protocol Buffers codec for durable persistent state log entries.
/// </summary>
public sealed class ProtobufStateOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableStateOperationCodec<T>
{
    private const uint SetCommand = 0;
    private const uint ClearCommand = 1;

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, IBufferWriter<byte> output)
    {
        var operation = new ProtobufStateOperation();
        operation.Command.Add(SetCommand);
        operation.State.Add(converter.ToByteString(state));
        operation.Version.Add(version);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        var operation = new ProtobufStateOperation();
        operation.Command.Add(ClearCommand);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableStateOperationHandler<T> consumer)
    {
        var operation = ProtobufGeneratedCodecHelpers.Parse(input, ProtobufStateOperation.Parser, "state operation");
        var command = ProtobufGeneratedCodecHelpers.RequireCommand(operation.Command);
        switch (command)
        {
            case SetCommand:
                consumer.ApplySet(
                    converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.State, "state", command)),
                    ProtobufGeneratedCodecHelpers.RequireUInt64(operation.Version, "version", command));
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
public sealed class ProtobufTcsOperationCodec<T>(
    ProtobufValueConverter<T> converter) : IDurableTaskCompletionSourceOperationCodec<T>
{
    private const uint PendingCommand = 0;
    private const uint CompletedCommand = 1;
    private const uint FaultedCommand = 2;
    private const uint CanceledCommand = 3;

    /// <inheritdoc/>
    public void WritePending(IBufferWriter<byte> output)
    {
        var operation = new ProtobufTaskCompletionSourceOperation();
        operation.Command.Add(PendingCommand);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, IBufferWriter<byte> output)
    {
        var operation = new ProtobufTaskCompletionSourceOperation();
        operation.Command.Add(CompletedCommand);
        operation.Value.Add(converter.ToByteString(value));
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, IBufferWriter<byte> output)
    {
        var operation = new ProtobufTaskCompletionSourceOperation();
        operation.Command.Add(FaultedCommand);
        operation.Message.Add(exception.Message);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void WriteCanceled(IBufferWriter<byte> output)
    {
        var operation = new ProtobufTaskCompletionSourceOperation();
        operation.Command.Add(CanceledCommand);
        operation.WriteTo(output);
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        var operation = ProtobufGeneratedCodecHelpers.Parse(input, ProtobufTaskCompletionSourceOperation.Parser, "task completion source operation");
        var command = ProtobufGeneratedCodecHelpers.RequireCommand(operation.Command);
        switch (command)
        {
            case PendingCommand:
                consumer.ApplyPending();
                break;
            case CompletedCommand:
                consumer.ApplyCompleted(converter.FromByteString(ProtobufGeneratedCodecHelpers.RequireBytes(operation.Value, "value", command)));
                break;
            case FaultedCommand:
                consumer.ApplyFaulted(new Exception(ProtobufGeneratedCodecHelpers.RequireString(operation.Message, "message", command)));
                break;
            case CanceledCommand:
                consumer.ApplyCanceled();
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }
    }
}
