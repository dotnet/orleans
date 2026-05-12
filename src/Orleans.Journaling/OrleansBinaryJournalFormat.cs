using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
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

    IJournalBatchWriter IJournalFormat.CreateWriter() => new OrleansBinaryJournalBatchWriter();

    void IJournalFormat.Read(JournalReadBuffer input, IStateResolver resolver, in JournaledStateReplayContext context) =>
        OrleansBinaryJournalReader.Read(input, resolver, _sessionPool, in context);
}

internal static class OrleansBinaryJournalReader
{
    internal const byte FormatVersion = 0;

    public static void Read(JournalReadBuffer input, IStateResolver resolver, SerializerSessionPool sessionPool, in JournaledStateReplayContext context)
    {
        ArgumentNullException.ThrowIfNull(resolver);
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
            using var batchSlice = input.PeekSlice(input.Length);
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

            ulong streamIdValue;
            try
            {
                streamIdValue = reader.ReadVarUInt64();
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: truncated varuint64 state id.",
                    exception);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Malformed binary journal entry stream at byte offset {offset}: malformed varuint64 state id.",
                    exception);
            }

            var streamId = new JournalStreamId(streamIdValue);
            var state = resolver.ResolveState(streamId);

            // Slice the operation payload (post-streamId) so the state receives exactly one operation body.
            var operationStart = (int)reader.Position;
            var operationLength = frameLength - operationStart;
            using var operationSlice = batchSlice.Slice(operationStart, operationLength);

            try
            {
                state.ApplyOperation(
                    new JournalOperation(OrleansBinaryJournalFormat.JournalFormatKey, operationSlice.AsReadOnlySequence()),
                    in context);
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
