using System.Buffers;
using global::MessagePack;

namespace Orleans.Journaling.MessagePack;

/// <summary>
/// MessagePack codec for durable value log entries.
/// </summary>
public sealed class MessagePackValueOperationCodec<T>(MessagePackSerializerOptions options) : IDurableValueOperationCodec<T>
{
    private const int SetCommand = 0;

    public void WriteSet(T value, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteSetPayload(value, output));

    private void WriteSetPayload(T value, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(SetCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, value, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer)
    {
        var reader = new MessagePackOperationReader(input);
        reader.RequireOperandCount(1);
        switch (reader.Command)
        {
            case SetCommand:
                consumer.ApplySet(reader.ReadValue<T>(options));
                break;
            default:
                throw new NotSupportedException($"Command type {reader.Command} is not supported");
        }

        reader.EnsureEnd();
    }
}

/// <summary>
/// MessagePack codec for durable persistent state log entries.
/// </summary>
public sealed class MessagePackStateOperationCodec<T>(MessagePackSerializerOptions options) : IDurableStateOperationCodec<T>
{
    private const int SetCommand = 0;
    private const int ClearCommand = 1;

    public void WriteSet(T state, ulong version, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteSetPayload(state, version, output));

    private void WriteSetPayload(T state, ulong version, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(3);
        writer.Write(SetCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, state, options);
        writer.Write(version);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteClear(LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, WriteClearPayload);

    private static void WriteClearPayload(IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(ClearCommand);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableStateOperationHandler<T> consumer)
    {
        var reader = new MessagePackOperationReader(input);
        switch (reader.Command)
        {
            case SetCommand:
                reader.RequireOperandCount(2);
                consumer.ApplySet(reader.ReadValue<T>(options), reader.ReadUInt64());
                break;
            case ClearCommand:
                reader.RequireOperandCount(0);
                consumer.ApplyClear();
                break;
            default:
                throw new NotSupportedException($"Command type {reader.Command} is not supported");
        }

        reader.EnsureEnd();
    }
}

/// <summary>
/// MessagePack codec for durable task completion source log entries.
/// </summary>
public sealed class MessagePackTcsOperationCodec<T>(MessagePackSerializerOptions options) : IDurableTaskCompletionSourceOperationCodec<T>
{
    private const int PendingCommand = 0;
    private const int CompletedCommand = 1;
    private const int FaultedCommand = 2;
    private const int CanceledCommand = 3;

    public void WritePending(LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteCommand(PendingCommand, output));

    public void WriteCompleted(T value, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteCompletedPayload(value, output));

    private void WriteCompletedPayload(T value, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(CompletedCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, value, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteFaulted(Exception exception, LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteFaultedPayload(exception, output));

    private static void WriteFaultedPayload(Exception exception, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(FaultedCommand);
        writer.Write(exception.Message);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteCanceled(LogStreamWriter writer) =>
        MessagePackOperationCodecWriter.Write(writer, output => WriteCommand(CanceledCommand, output));

    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        var reader = new MessagePackOperationReader(input);
        switch (reader.Command)
        {
            case PendingCommand:
                reader.RequireOperandCount(0);
                consumer.ApplyPending();
                break;
            case CompletedCommand:
                reader.RequireOperandCount(1);
                consumer.ApplyCompleted(reader.ReadValue<T>(options));
                break;
            case FaultedCommand:
                reader.RequireOperandCount(1);
                consumer.ApplyFaulted(new Exception(reader.ReadString()));
                break;
            case CanceledCommand:
                reader.RequireOperandCount(0);
                consumer.ApplyCanceled();
                break;
            default:
                throw new NotSupportedException($"Command type {reader.Command} is not supported");
        }

        reader.EnsureEnd();
    }

    private static void WriteCommand(int command, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(command);
        MessagePackCodecHelpers.Flush(ref writer);
    }

}
