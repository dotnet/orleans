using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal sealed class DurableEnvelopeLifecycle : IDurableDictionaryValueLifecycle<DurableEnvelope>
{
    public DurableEnvelope Retain(DurableEnvelope value) => value.Retain();
    public void Release(DurableEnvelope value) => value.Dispose();
}

internal sealed class InboxDeadLetterLifecycle : IDurableDictionaryValueLifecycle<InboxDeadLetter>
{
    public InboxDeadLetter Retain(InboxDeadLetter value) => RetainValue(value);
    internal static InboxDeadLetter RetainValue(InboxDeadLetter value) => new()
    {
        Envelope = value.Envelope.Retain(),
        DeadLetteredAt = value.DeadLetteredAt,
        Reason = value.Reason,
        AttemptCount = value.AttemptCount
    };
    public void Release(InboxDeadLetter value) => value.Envelope.Dispose();
}

internal sealed class OutboxDeadLetterLifecycle : IDurableDictionaryValueLifecycle<OutboxDeadLetter>
{
    public OutboxDeadLetter Retain(OutboxDeadLetter value) => RetainValue(value);
    internal static OutboxDeadLetter RetainValue(OutboxDeadLetter value) => new()
    {
        Envelope = value.Envelope.Retain(),
        DeadLetteredAt = value.DeadLetteredAt,
        Reason = value.Reason,
        AttemptCount = value.AttemptCount
    };
    public void Release(OutboxDeadLetter value) => value.Envelope.Dispose();
}
