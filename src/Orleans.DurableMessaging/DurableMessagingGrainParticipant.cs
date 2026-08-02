using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingGrainParticipant(
    IDurableInbox inbox,
    IDurableOutbox outbox,
    IDurableMessageScheduler scheduler) : IJournaledGrainParticipant
{
    public void Initialize()
    {
        _ = inbox;
        _ = outbox;
        _ = scheduler;
    }
}
