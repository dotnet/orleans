using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableMessaging;

internal static class MessagingConfiguration
{
    internal static void Configure(ISiloBuilder siloBuilder)
    {
        // <messaging_registration>
        siloBuilder.UseInMemoryDurableJobs();
        siloBuilder.AddVolatileJournalStorage();
        siloBuilder.AddDurableMessaging();
        // </messaging_registration>
    }
}

// <messaging_grain>
public interface INotificationGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<int> GetCount();
}

public sealed class NotificationGrain : Grain, INotificationGrain, IInboxHandler
{
    private readonly IDurableValue<int> _count;

    public NotificationGrain(
        IDurableInbox inbox,
        [FromKeyedServices("notification-count")] IDurableValue<int> count)
    {
        _count = count;
        inbox.RegisterHandler("notifications", this);
    }

    public ValueTask<int> GetCount() => new(_count.Value);

    public bool CanHandle(IInboxHandlerContext context) =>
        context.Envelope.RouteKey == "notifications";

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (!context.Envelope.Data.TryGetBody<string>(out var message)
            || string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A notification must contain a nonempty string.");
        }

        var nextCount = checked(_count.Value + 1);
        _count.Value = nextCount;
        return ValueTask.CompletedTask;
    }
}
// </messaging_grain>
