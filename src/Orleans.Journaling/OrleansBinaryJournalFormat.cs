using System.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryJournalFormat : IJournalFormat
{
    internal const string JournalFormatKey = "orleans-binary";

    public static OrleansBinaryJournalFormat Instance { get; } = new();

    private OrleansBinaryJournalFormat()
    {
    }

    public string FileExtension => ".obs";

    public string? MimeType => "application/octet-stream";

    IJournalBatchWriter IJournalFormat.CreateWriter() => new OrleansBinaryJournalBatchWriter();

    void IJournalFormat.Read(JournalReadBuffer input, IStateResolver resolver) => OrleansBinaryJournalReader.Read(input, resolver);
}

internal static class OrleansBinaryJournalReader
{
    public static void Read(JournalReadBuffer input, IStateResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var offset = 0L;
        while (TryReadEntry(input, resolver, offset, out var frameLength))
        {
            offset += frameLength;
        }
    }

    private static bool TryReadEntry(JournalReadBuffer input, IStateResolver resolver, long offset, out long frameLength)
    {
        frameLength = 0;
        if (input.Length == 0)
        {
            return false;
        }

        using var available = input.PeekSlice(input.Length);
        var remaining = available.AsReadOnlySequence();
        if (!OrleansBinaryJournalEntryFrameReader.TryReadEntry(
            ref remaining,
            offset,
            input.IsCompleted,
            out var streamId,
            out var payload,
            out frameLength,
            out _))
        {
            return false;
        }

        if (frameLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: entry length exceeds maximum supported frame size.");
        }

        input.Skip((int)frameLength);
        var state = resolver.ResolveState(streamId);
        if (state is IFormattedJournalEntryBuffer formattedEntryBuffer)
        {
            formattedEntryBuffer.AddFormattedEntry(new OrleansBinaryFormattedJournalEntry(payload));
        }
        else if (state is not IDurableNothing)
        {
            ApplyEntry(payload, state, streamId.Value);
        }

        return true;
    }

    internal static void ApplyEntry(ReadOnlySequence<byte> payload, IJournaledState state) =>
        ApplyEntry(payload, state, streamId: null);

    private static void ApplyEntry(ReadOnlySequence<byte> payload, IJournaledState state, ulong? streamId)
    {
        if (state is IDurableNothing)
        {
            return;
        }

        var operationCodec = state.OperationCodec;
        if (operationCodec is not IOrleansBinaryJournalEntryCodec binaryCodec)
        {
            var streamDescription = streamId is { } value ? $" for stream {value}" : "";
            var codecType = operationCodec?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"The Orleans binary journal entry{streamDescription} resolved to state " +
                $"'{state.GetType().FullName}', but its codec '{codecType}' does not implement IOrleansBinaryJournalEntryCodec.");
        }

        binaryCodec.Apply(payload, state);
    }
}

internal static class OrleansBinaryJournalEntryFrameReader
{
    public static bool TryReadEntry(
        ref ReadOnlySequence<byte> remaining,
        long offset,
        bool isCompleted,
        out JournalStreamId streamId,
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
                $"Malformed binary journal entry stream at byte offset {offset}: malformed varuint32 entry length prefix.",
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
                $"Malformed binary journal entry stream at byte offset {offset}: truncated varuint32 entry length prefix.");
        }

        if (bodyLength == 0)
        {
            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: zero-length entries are not valid.");
        }

        if (bodyLength > (ulong)reader.Remaining)
        {
            if (!isCompleted)
            {
                minimumBufferLength = bodyLength <= int.MaxValue - lengthPrefixSize ? lengthPrefixSize + checked((int)bodyLength) : null;
                return false;
            }

            throw new InvalidOperationException(
                $"Malformed binary journal entry stream at byte offset {offset}: entry length {bodyLength} exceeds remaining input bytes {reader.Remaining}.");
        }

        var body = remaining.Slice(lengthPrefixSize, bodyLength);
        var bodyReader = new SequenceReader<byte>(body);
        var id = ReadJournalStreamId(ref bodyReader, offset);
        payload = body.Slice(bodyReader.Consumed);
        streamId = new(id);
        frameLength = checked(lengthPrefixSize + (long)bodyLength);
        return true;
    }

    private static ulong ReadJournalStreamId(ref SequenceReader<byte> reader, long offset)
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
                $"Malformed binary journal entry stream at byte offset {offset}: malformed varuint64 state id.",
                exception);
        }

        throw new InvalidOperationException(
            $"Malformed binary journal entry stream at byte offset {offset}: truncated varuint64 state id.");
    }
}
