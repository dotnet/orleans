using System.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryLogFormat : ILogFormat
{
    internal const string LogFormatKey = "orleans-binary";

    public static OrleansBinaryLogFormat Instance { get; } = new();

    private OrleansBinaryLogFormat()
    {
    }

    ILogBatchWriter ILogFormat.CreateWriter() => new OrleansBinaryLogBatchWriter();

    void ILogFormat.Read(LogReadBuffer input, IStateMachineResolver resolver) => OrleansBinaryLogReader.Read(input, resolver);
}

internal static class OrleansBinaryLogReader
{
    public static void Read(LogReadBuffer input, IStateMachineResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        while (TryReadEntry(input, resolver))
        {
        }
    }

    private static bool TryReadEntry(LogReadBuffer input, IStateMachineResolver resolver)
    {
        if (input.Length == 0)
        {
            return false;
        }

        using var available = input.PeekSlice(input.Length);
        var remaining = available.AsReadOnlySequence();
        if (!OrleansBinaryLogEntryFrameReader.TryReadEntry(
            ref remaining,
            offset: 0,
            input.IsCompleted,
            out var streamId,
            out var payload,
            out var frameLength,
            out _))
        {
            return false;
        }

        if (frameLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                "Malformed binary log entry stream at byte offset 0: entry length exceeds maximum supported frame size.");
        }

        input.Skip((int)frameLength);
        var stateMachine = resolver.ResolveStateMachine(streamId);
        if (stateMachine is IFormattedLogEntryBuffer formattedEntryBuffer)
        {
            formattedEntryBuffer.AddFormattedEntry(new OrleansBinaryFormattedLogEntry(payload));
        }
        else if (stateMachine is not IDurableNothing)
        {
            ApplyEntry(payload, stateMachine, streamId.Value);
        }

        return true;
    }

    internal static void ApplyEntry(ReadOnlySequence<byte> payload, IDurableStateMachine stateMachine) =>
        ApplyEntry(payload, stateMachine, streamId: null);

    private static void ApplyEntry(ReadOnlySequence<byte> payload, IDurableStateMachine stateMachine, ulong? streamId)
    {
        if (stateMachine is IDurableNothing)
        {
            return;
        }

        var operationCodec = stateMachine.OperationCodec;
        if (operationCodec is not IOrleansBinaryLogEntryCodec binaryCodec)
        {
            var streamDescription = streamId is { } value ? $" for stream {value}" : "";
            var codecType = operationCodec?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"The Orleans binary log entry{streamDescription} resolved to state machine " +
                $"'{stateMachine.GetType().FullName}', but its codec '{codecType}' does not implement IOrleansBinaryLogEntryCodec.");
        }

        binaryCodec.Apply(payload, stateMachine);
    }
}

internal static class OrleansBinaryLogEntryFrameReader
{
    public static bool TryReadEntry(
        ref ReadOnlySequence<byte> remaining,
        long offset,
        bool isCompleted,
        out LogStreamId streamId,
        out ReadOnlySequence<byte> payload,
        out long frameLength,
        out int? minimumBufferLength)
    {
        streamId = default;
        payload = default;
        frameLength = 0;
        minimumBufferLength = null;

        if (remaining.IsEmpty)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(remaining);
        uint bodyLength;
        int lengthPrefixSize;
        int minimumLengthPrefixSize;
        bool readLength;
        try
        {
            readLength = VarIntHelper.TryReadVarUInt32(ref reader, out bodyLength, out lengthPrefixSize, out minimumLengthPrefixSize);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: malformed varuint32 entry length prefix.",
                exception);
        }

        if (!readLength)
        {
            if (!isCompleted)
            {
                minimumBufferLength = minimumLengthPrefixSize;
                return false;
            }

            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: truncated varuint32 entry length prefix.");
        }

        if (bodyLength == 0)
        {
            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: zero-length entries are not valid.");
        }

        if (bodyLength > (ulong)reader.Remaining)
        {
            if (!isCompleted)
            {
                minimumBufferLength = bodyLength <= int.MaxValue - lengthPrefixSize ? lengthPrefixSize + checked((int)bodyLength) : null;
                return false;
            }

            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: entry length {bodyLength} exceeds remaining input bytes {reader.Remaining}.");
        }

        var body = remaining.Slice(lengthPrefixSize, bodyLength);
        var bodyReader = new SequenceReader<byte>(body);
        var id = ReadLogStreamId(ref bodyReader, offset);
        payload = body.Slice(bodyReader.Consumed);
        streamId = new(id);
        frameLength = checked(lengthPrefixSize + (long)bodyLength);
        return true;
    }

    private static ulong ReadLogStreamId(ref SequenceReader<byte> reader, long offset)
    {
        try
        {
            if (VarIntHelper.TryReadVarUInt64(ref reader, out var result, out _, out _))
            {
                return result;
            }
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Malformed binary log entry stream at byte offset {offset}: malformed varuint64 state-machine id.",
                exception);
        }

        throw new InvalidOperationException(
            $"Malformed binary log entry stream at byte offset {offset}: truncated varuint64 state-machine id.");
    }
}
