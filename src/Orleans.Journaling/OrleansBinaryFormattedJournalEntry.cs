using System.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryFormattedJournalEntry : IFormattedJournalEntry
{
    public OrleansBinaryFormattedJournalEntry(ReadOnlySequence<byte> payload)
    {
        // Retired states retain formatted entries after the read buffer is released;
        // this format-specific type also prevents cross-codec-family replay.
        Payload = payload.ToArray();
    }

    public ReadOnlyMemory<byte> Payload { get; }

    public void Apply(IJournaledState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        OrleansBinaryJournalReader.ApplyEntry(new ReadOnlySequence<byte>(Payload), state);
    }
}
