using System.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryFormattedLogEntry : IFormattedLogEntry
{
    public OrleansBinaryFormattedLogEntry(ReadOnlySequence<byte> payload)
    {
        // Retired state machines retain formatted entries after the read buffer is released;
        // this format-specific type also prevents cross-codec-family replay.
        Payload = payload.ToArray();
    }

    public ReadOnlyMemory<byte> Payload { get; }
}
