using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryPreservedJournalOperation : IPreservedJournalOperation
{
    private readonly byte[] _payload;

    public OrleansBinaryPreservedJournalOperation(ArcBuffer payload, SerializerSessionPool sessionPool)
    {
        ArgumentNullException.ThrowIfNull(sessionPool);
        // Retired states retain preserved operations after the read buffer is released; copy the bytes.
        _payload = payload.ToArray();
    }

    public ReadOnlyMemory<byte> Payload => _payload;

    public string FormatKey => OrleansBinaryJournalFormat.JournalFormatKey;
}
