namespace Orleans.Messaging.Tests.Support;

internal sealed class OutboxStateTestAccess(IOutbox outbox)
{
    private readonly Outbox _outbox = (Outbox)outbox;

    public IEnumerable<HierarchicalKey> AttemptKeys => _outbox.MessageState.Keys;
    public IEnumerable<HierarchicalKey> DeadLetterKeys => _outbox.DeadLetterState.Keys;
    public OutboxMessageState Attempt(HierarchicalKey id) => _outbox.MessageState[id].State;
    public OutboxDeadLetter DeadLetter(HierarchicalKey id) => _outbox.DeadLetterState[id];
}
