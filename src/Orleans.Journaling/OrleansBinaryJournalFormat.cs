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

    void IJournalFormat.Read(JournalReadBuffer input, IStateResolver resolver) =>
        OrleansBinaryJournalReader.Read(input, resolver, _sessionPool);
}

internal static class OrleansBinaryJournalReader
{
    internal const byte FormatVersion = 0;

    public static void Read(JournalReadBuffer input, IStateResolver resolver, SerializerSessionPool sessionPool)
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

            // Slice the operation payload (post-streamId) so the codec receives a length-bounded reader.
            // Tying the codec's reader to its own operation lets the framework verify post-call that the
            // codec consumed exactly the right number of bytes via reader.Position == reader.Length.
            var operationStart = (int)reader.Position;
            var operationLength = frameLength - operationStart;
            using var operationSlice = batchSlice.Slice(operationStart, operationLength);

            try
            {
                if (state is IFormattedJournalEntryBuffer formattedEntryBuffer)
                {
                    formattedEntryBuffer.AddFormattedEntry(new OrleansBinaryFormattedJournalEntry(operationSlice, sessionPool));
                }
                else if (state is not IDurableNothing)
                {
                    var operationReader = Reader.Create(operationSlice, session);
                    ApplyEntry(ref operationReader, resolver.GetOperationCodec(state), state, streamIdValue);
                }
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

    /// <summary>
    /// Applies a single binary journal operation. The <paramref name="reader"/> must be bounded to the
    /// operation body (the bytes immediately after the stream-id varuint, starting with the format-version
    /// byte). After the codec returns, the framework verifies that <c>reader.Position == reader.Length</c>.
    /// </summary>
    internal static void ApplyEntry(ref Reader<ArcBufferReaderInput> reader, object? operationCodec, IJournaledState state, ulong? streamId = null)
    {
        if (operationCodec is not IOrleansBinaryJournalEntryCodec binaryCodec)
        {
            var streamDescription = streamId is { } value ? $" for stream {value}" : "";
            var codecType = operationCodec?.GetType().FullName ?? "<null>";
            throw new InvalidOperationException(
                $"The Orleans binary journal entry{streamDescription} resolved to state " +
                $"'{state.GetType().FullName}', but its codec '{codecType}' does not implement IOrleansBinaryJournalEntryCodec.");
        }

        // Each operation is independently encoded — clear back-reference state between operations
        // so behaviour matches the prior per-operation session rental.
        reader.Session.Reset();

        binaryCodec.Apply(ref reader, state);

        if (reader.Position != reader.Length)
        {
            var streamDescription = streamId is { } value ? $" for stream {value}" : "";
            throw new InvalidOperationException(
                $"Binary journal codec '{operationCodec.GetType().FullName}'{streamDescription} did not consume the complete entry body: " +
                $"position {reader.Position}, expected {reader.Length}.");
        }
    }
}
