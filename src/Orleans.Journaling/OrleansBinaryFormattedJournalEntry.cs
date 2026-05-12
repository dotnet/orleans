using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryFormattedJournalEntry : IFormattedJournalEntry
{
    private readonly SerializerSessionPool _sessionPool;
    private readonly byte[] _payload;

    public OrleansBinaryFormattedJournalEntry(ArcBuffer payload, SerializerSessionPool sessionPool)
    {
        ArgumentNullException.ThrowIfNull(sessionPool);
        // Retired states retain formatted entries after the read buffer is released; copy the bytes
        // and hold a ref to the session pool so we can replay later when the state is re-registered.
        _payload = payload.ToArray();
        _sessionPool = sessionPool;
    }

    public ReadOnlyMemory<byte> Payload => _payload;

    public string FormatKey => OrleansBinaryJournalFormat.JournalFormatKey;

    public void Apply(IJournaledState state, object operationCodec)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state is IDurableNothing)
        {
            return;
        }

        // Re-materialise the captured bytes into an ArcBuffer-backed Reader and dispatch through
        // the same code path used at recovery time.
        using var writer = new ArcBufferWriter();
        writer.Write(_payload);
        using var slice = writer.PeekSlice(writer.Length);
        using var session = _sessionPool.GetSession();
        var reader = Reader.Create(slice, session);
        OrleansBinaryJournalReader.ApplyEntry(ref reader, operationCodec, state);
    }
}
