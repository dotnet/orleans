namespace Orleans.Messaging.Tests.Support;

internal sealed class OutboxStateTestAccess(IOutbox outbox)
{
    private readonly Outbox _outbox = (Outbox)outbox;

    public IEnumerable<MessageId> AttemptKeys => _outbox.MessageState.Keys;
    public IEnumerable<MessageId> DeadLetterKeys => _outbox.DeadLetterState.Keys;
    public OutboxMessageState Attempt(MessageId id) => _outbox.MessageState[id].State;
    public OutboxDeadLetter DeadLetter(MessageId id) => _outbox.DeadLetterState[id];
}
