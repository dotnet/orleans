using System.Threading.Tasks;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingGrainParticipant(
    IJournaledStateManager stateManager,
    IGrainContext grainContext,
    IDurableInbox inbox,
    IDurableOutbox outbox,
    DurableMessagingJournalObserver observer) : IJournaledGrainParticipant
{
    public void Initialize()
    {
        _ = inbox;
        _ = outbox;
        DurableMessagingStateManagerCapabilities.RegisterObserver(stateManager, observer);
        grainContext.ObservableLifecycle.Subscribe(
            nameof(DurableMessagingGrainParticipant),
            GrainLifecycleStage.First,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                DurableMessagingActivationValidator.Validate(grainContext);
                return Task.CompletedTask;
            });
    }
}
