using System.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryFormattedJournalEntry : IFormattedJournalEntry
{
    public OrleansBinaryFormattedJournalEntry(ReadOnlySequence<byte> payload)
    {
        // Retired state machines retain formatted entries after the read buffer is released;
        // this format-specific type also prevents cross-codec-family replay.
        Payload = payload.ToArray();
    }

    public ReadOnlyMemory<byte> Payload { get; }

    public void Apply(IDurableStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        OrleansBinaryJournalReader.ApplyEntry(new ReadOnlySequence<byte>(Payload), stateMachine);
    }
}
