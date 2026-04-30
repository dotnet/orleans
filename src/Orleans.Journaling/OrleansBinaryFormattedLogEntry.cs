using System.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryFormattedLogEntry : IFormattedLogEntry
{
    public OrleansBinaryFormattedLogEntry(ReadOnlySequence<byte> payload)
    {
        Payload = payload.ToArray();
    }

    public OrleansBinaryFormattedLogEntry(ReadOnlyMemory<byte> payload)
    {
        Payload = payload.ToArray();
    }

    public ReadOnlyMemory<byte> Payload { get; }
}
