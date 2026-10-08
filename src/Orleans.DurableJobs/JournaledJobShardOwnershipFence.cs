using Orleans.Journaling;

namespace Orleans.DurableJobs;

internal sealed class JournaledJobShardOwnershipFence(IDurableValueCommandCodec<string> codec)
    : IStateMachine, IDurableValueCommandHandler<string>
{
    public const string StateName = "ownership";

    private JournalStreamWriter _writer;
    private string? _owner;

    public void Write(string owner)
    {
        _owner = owner;
        // Repeating the owner still advances the storage concurrency token.
        codec.WriteSet(owner, _writer);
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context)
        => context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);

    void IDurableValueCommandHandler<string>.ApplySet(string value) => _owner = value;

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _owner = null;
        _writer = writer;
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        if (_owner is not null)
        {
            codec.WriteSet(_owner, writer);
        }
    }
}
