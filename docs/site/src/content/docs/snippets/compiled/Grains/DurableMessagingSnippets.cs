using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;

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

    public async ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (!context.Envelope.Data.TryGetBody<string>(out var message)
            || string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A notification must contain a nonempty string.");
        }

        IPreparedOutboxBatch? reply = null;
        if (context.Envelope.ReplyTo is { } recipient)
        {
            var envelope = context.CreateEnvelope()
                .To(recipient, "notifications/received")
                .WithBody(message)
                .Build();
            reply = await context.Outbox.PrepareSendAsync([envelope], cancellationToken);
        }

        var nextCount = checked(_count.Value + 1);
        return () =>
        {
            _count.Value = nextCount;
            if (reply is not null)
            {
                context.Send(reply);
            }
        };
    }
}
// </messaging_grain>

// <messaging_send>
public interface INotificationSenderGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task SendAsync(GrainId receiver, string message);
}

public sealed class NotificationSenderGrain(
    IDurableOutbox outbox,
    IDurableStateManager stateManager,
    [FromKeyedServices("sent-count")] IDurableValue<int> sentCount,
    SerializerSessionPool sessions) : Grain, INotificationSenderGrain
{
    public async Task SendAsync(GrainId receiver, string message)
    {
        var envelope = new DurableEnvelopeBuilder(sessions, this.GetGrainId())
            .To(receiver, "notifications")
            .WithBody(message)
            .Build();
        using var batch = await outbox.PrepareSendAsync([envelope]);

        sentCount.Value = checked(sentCount.Value + 1);
        outbox.Send(batch);
        await stateManager.WriteStateAsync();
    }
}
// </messaging_send>
