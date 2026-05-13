using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryJournalFormat : IJournalFormat
{
    internal const string JournalFormatKey = "orleans-binary";

    private readonly SerializerSessionPool _sessionPool;

    public OrleansBinaryJournalFormat(SerializerSessionPool sessionPool)
    {
        ArgumentNullException.ThrowIfNull(sessionPool);
        _sessionPool = sessionPool;
    }

    public string FormatKey => JournalFormatKey;

    public string? MimeType => "application/octet-stream";

    internal SerializerSessionPool SessionPool => _sessionPool;

    JournalBufferWriter IJournalFormat.CreateWriter() => new OrleansBinaryJournalBufferWriter();

    void IJournalFormat.Replay(JournalBufferReader input, JournalReplayContext context) =>
        OrleansBinaryJournalReader.Read(input, _sessionPool, context);
}

internal static class OrleansBinaryJournalReader
{
    internal const byte FormatVersion = 0;

    public static void Read(JournalBufferReader input, SerializerSessionPool sessionPool, JournalReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(sessionPool);

        if (input.Length == 0)
        {
            return;
        }

        using var session = sessionPool.GetSession();
        var offset = 0L;

        while (input.Length > 0)
        {
            // Snapshot all currently-buffered bytes as a pinned ArcBuffer that the reader walks
            // for this iteration. Successful entries are committed by Skip()ing the underlying
            // ArcBufferReader; truncated tails leave bytes for a subsequent call.
            using var batchSlice = input.Peek(input.Length);
            var reader = Reader.Create(batchSlice, session);

            uint bodyLength;
            try
            {
                bodyLength = reader.ReadVarUInt32();
            }
            catch (InvalidOperationException) when (!input.IsCompleted)
            {
                return;
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: truncated varuint32 entry length prefix.",
                    exception);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: malformed varuint32 entry length prefix.",
                    exception);
            }

            if (bodyLength == 0)
            {
                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: zero-length entries are not valid.");
            }

            var lengthPrefixSize = (int)reader.Position;
            var availableBody = batchSlice.Length - lengthPrefixSize;
            if (bodyLength > (ulong)availableBody)
            {
                if (!input.IsCompleted)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: entry length {bodyLength} exceeds remaining input bytes {availableBody}.");
            }

            var frameLength = checked(lengthPrefixSize + (int)bodyLength);

            uint streamIdValue;
            try
            {
                streamIdValue = reader.ReadVarUInt32();
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: truncated varuint32 state id.",
                    exception);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: malformed varuint32 state id.",
                    exception);
            }

            var streamId = new JournalStreamId(streamIdValue);
            var state = context.ResolveState(streamId);

            // Slice the entry payload (post-streamId) so the state receives exactly one command body.
            var payloadStart = (int)reader.Position;
            var payloadLength = frameLength - payloadStart;
            using var payloadSlice = batchSlice.Slice(payloadStart, payloadLength);
            using var payloadBuffer = new ArcBufferWriter();
            payloadBuffer.AppendPinned(payloadSlice);

            try
            {
                state.ReplayEntry(
                    new JournalEntry(OrleansBinaryJournalFormat.JournalFormatKey, new JournalBufferReader(new ArcBufferReader(payloadBuffer), isCompleted: true)),
                    context);
            }
            catch (Exception exception) when (exception is not InvalidOperationException ioe || !ioe.Message.StartsWith("Malformed binary journal entry stream", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Failed to apply binary journal entry at byte offset {offset} for stream {streamIdValue}: {exception.Message}",
                    exception);
            }

            input.Skip(frameLength);
            offset += frameLength;
        }
    }
}
