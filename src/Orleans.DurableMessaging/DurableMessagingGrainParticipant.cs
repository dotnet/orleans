using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingGrainParticipant(
    IJournaledStateManager stateManager,
    IDurableInbox inbox,
    IDurableOutbox outbox,
    DurableMessagingJournalObserver observer) : IJournaledGrainParticipant
{
    public void Initialize()
    {
        _ = inbox;
        _ = outbox;
        DurableMessagingStateManagerCapabilities.RegisterObserver(stateManager, observer);
    }
}
