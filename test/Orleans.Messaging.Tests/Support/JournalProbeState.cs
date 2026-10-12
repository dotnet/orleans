using Orleans.Journaling;

namespace Orleans.Messaging.Tests.Support;

// An empty journal state which lets tests fail write preparation or observe write acknowledgement.
internal sealed class JournalProbeState : IStateMachine
{
    public const string Name = "test-journal-probe";

    public JournalProbeState(IJournaledStateManager manager) => manager.RegisterStateMachine(Name, this);

    public Exception? PreparationFailure { get; set; }
    public Action? WriteCompleted { get; set; }

    public ValueTask PrepareWriteAsync(CancellationToken cancellationToken = default) =>
        PreparationFailure is { } failure ? ValueTask.FromException(failure) : default;

    public void OnWriteCompleted() => WriteCompleted?.Invoke();
    public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        throw new NotSupportedException("The journal probe has no entries.");
    public void Reset(JournalStreamWriter writer) { }
    public void WritePendingEntries(JournalStreamWriter writer) { }
    public void WriteSnapshot(JournalStreamWriter writer) { }
}
