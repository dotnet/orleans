using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingGrainParticipant(
    IServiceProvider serviceProvider) : IJournaledGrainParticipant
{
    public void Initialize()
    {
        var inbox = serviceProvider.GetRequiredService<DurableInboxExtension>();
        _ = serviceProvider.GetRequiredService<IDurableOutbox>();
        _ = serviceProvider.GetRequiredService<IDurableMessageScheduler>();
        var grainContext = serviceProvider.GetRequiredService<IGrainContext>();
        grainContext.ObservableLifecycle.Subscribe(
            nameof(DurableInboxExtension),
            GrainLifecycleStage.Last,
            inbox);
    }
}
