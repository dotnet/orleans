using System.Buffers;
using global::MessagePack;

namespace Orleans.Journaling.MessagePack;

/// <summary>
/// MessagePack codec for durable value log entries.
/// </summary>
public sealed class MessagePackValueEntryCodec<T>(MessagePackSerializerOptions options) : IDurableValueCodec<T>
{
    private const int SetCommand = 0;

    public void WriteSet(T value, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(SetCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, value, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableValueLogEntryConsumer<T> consumer)
    {
        var reader = new MessagePackReader(input);
        var command = MessagePackCodecHelpers.ReadCommand(ref reader, expectedItemCount: 2);
        switch (command)
        {
            case SetCommand:
                consumer.ApplySet(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }

        MessagePackCodecHelpers.RequireNoTrailingData(ref reader);
    }
}

/// <summary>
/// MessagePack codec for durable persistent state log entries.
/// </summary>
public sealed class MessagePackStateEntryCodec<T>(MessagePackSerializerOptions options) : IDurableStateCodec<T>
{
    private const int SetCommand = 0;
    private const int ClearCommand = 1;

    public void WriteSet(T state, ulong version, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(3);
        writer.Write(SetCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, state, options);
        writer.Write(version);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteClear(IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(ClearCommand);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void Apply(ReadOnlySequence<byte> input, IDurableStateLogEntryConsumer<T> consumer)
    {
        var reader = new MessagePackReader(input);
        var itemCount = reader.ReadArrayHeader();
        if (itemCount == 0)
        {
            throw new InvalidOperationException("Malformed MessagePack log entry: missing command.");
        }

        var command = reader.ReadInt32();
        switch (command)
        {
            case SetCommand:
                RequireItemCount(itemCount, 3, command);
                consumer.ApplySet(MessagePackCodecHelpers.ReadValue<T>(ref reader, options), reader.ReadUInt64());
                break;
            case ClearCommand:
                RequireItemCount(itemCount, 1, command);
                consumer.ApplyClear();
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }

        MessagePackCodecHelpers.RequireNoTrailingData(ref reader);
    }

    private static void RequireItemCount(int actual, int expected, int command)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {command} expected {expected} item(s), found {actual}.");
        }
    }
}

/// <summary>
/// MessagePack codec for durable task completion source log entries.
/// </summary>
public sealed class MessagePackTcsEntryCodec<T>(MessagePackSerializerOptions options) : IDurableTaskCompletionSourceCodec<T>
{
    private const int PendingCommand = 0;
    private const int CompletedCommand = 1;
    private const int FaultedCommand = 2;
    private const int CanceledCommand = 3;

    public void WritePending(IBufferWriter<byte> output) => WriteCommand(PendingCommand, output);

    public void WriteCompleted(T value, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(CompletedCommand);
        MessagePackCodecHelpers.WriteValue(ref writer, value, options);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteFaulted(Exception exception, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(2);
        writer.Write(FaultedCommand);
        writer.Write(exception.Message);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    public void WriteCanceled(IBufferWriter<byte> output) => WriteCommand(CanceledCommand, output);

    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceLogEntryConsumer<T> consumer)
    {
        var reader = new MessagePackReader(input);
        var itemCount = reader.ReadArrayHeader();
        if (itemCount == 0)
        {
            throw new InvalidOperationException("Malformed MessagePack log entry: missing command.");
        }

        var command = reader.ReadInt32();
        switch (command)
        {
            case PendingCommand:
                RequireItemCount(itemCount, 1, command);
                consumer.ApplyPending();
                break;
            case CompletedCommand:
                RequireItemCount(itemCount, 2, command);
                consumer.ApplyCompleted(MessagePackCodecHelpers.ReadValue<T>(ref reader, options));
                break;
            case FaultedCommand:
                RequireItemCount(itemCount, 2, command);
                consumer.ApplyFaulted(new Exception(reader.ReadString()));
                break;
            case CanceledCommand:
                RequireItemCount(itemCount, 1, command);
                consumer.ApplyCanceled();
                break;
            default:
                throw new NotSupportedException($"Command type {command} is not supported");
        }

        MessagePackCodecHelpers.RequireNoTrailingData(ref reader);
    }

    private static void WriteCommand(int command, IBufferWriter<byte> output)
    {
        var writer = MessagePackCodecHelpers.CreateWriter(output);
        writer.WriteArrayHeader(1);
        writer.Write(command);
        MessagePackCodecHelpers.Flush(ref writer);
    }

    private static void RequireItemCount(int actual, int expected, int command)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {command} expected {expected} item(s), found {actual}.");
        }
    }
}
