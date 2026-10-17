using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Messaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.Messaging;

internal static class MessagingConfiguration
{
    internal static void Configure(ISiloBuilder siloBuilder)
    {
        // <messaging_registration>
        siloBuilder.UseInMemoryDurableJobs();
        siloBuilder.AddVolatileJournalStorage();
        siloBuilder.AddMessaging();
        siloBuilder.Services.AddSingleton<Messages>();
        // </messaging_registration>
    }
}

// <messaging_messages>
public sealed class Messages(Serializer serializer)
{
    public MessageType<Notify> Notify { get; } = new("notifications.notify.v1", serializer);
    public MessageType<NotificationReceived> NotificationReceived { get; } = new("notifications.received.v1", serializer);
    public MessageType<ReserveStock> ReserveStock { get; } = new("inventory.reserve.v1", serializer);
    public MessageType<Restock> Restock { get; } = new("inventory.restock.v1", serializer);
    public MessageType<ReservationResult> ReservationResult { get; } = new("inventory.reservation-result.v1", serializer);
    public MessageType<ChargePayment> ChargePayment { get; } = new("payments.charge.v1", serializer);
    public MessageType<PaymentResult> PaymentResult { get; } = new("payments.result.v1", serializer);
    public MessageType<StockSnapshot> StockSnapshot { get; } = new("inventory.snapshot.v1", serializer);
    public MessageType<Shipment> Shipment { get; } = new("shipments.manifest.v1", serializer);
    public MessageType<SubmitCheckout> SubmitCheckout { get; } = new("checkout.submit.v1", serializer);
    public MessageType<GracePeriodElapsed> GracePeriodElapsed { get; } = new("checkout.grace-period-elapsed.v1", serializer);
}
// </messaging_messages>

// <messaging_payload>
[GenerateSerializer]
public sealed record Notify(
    [property: Id(0)] string? Text,
    [property: Id(2)] GrainId? ResponseDestination = null);

[GenerateSerializer]
public sealed record NotificationReceived(
    [property: Id(0)] string Text,
    [property: Id(1)] MessageId CommandId);
// </messaging_payload>

// <messaging_grain>
public interface INotificationGrain : IGrainWithStringKey, IMessagingGrain
{
    ValueTask<int> GetCount();
}

public sealed class NotificationGrain(
    IInbox inbox,
    IOutbox outbox,
    Messages messages,
    [FromKeyedServices("notification-count")] IDurableValue<int> count)
    : Grain, INotificationGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.Register(messages.Notify, HandleNotification);
        return base.OnActivateAsync(cancellationToken);
    }

    public ValueTask<int> GetCount() => new(count.Value);

    private void HandleNotification(Notify message, IInboxHandlerContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Text);
        var nextCount = checked(count.Value + 1);
        if (message.ResponseDestination is { } recipient)
        {
            outbox.SendReply(messages.NotificationReceived, context, recipient,
                new NotificationReceived(message.Text, context.Message.Envelope.MessageId));
        }
        count.Value = nextCount;
        context.Complete();
    }
}
// </messaging_grain>

// <messaging_exclusive_commit>
public interface IStockLevelGrain : IGrainWithStringKey, IMessagingGrain
{
    ValueTask<int> GetStock();
}

public sealed class StockLevelGrain(
    IInbox inbox,
    Messages messages,
    [FromKeyedServices("stock-level")] IDurableValue<int> stock)
    : Grain, IStockLevelGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.Register(messages.Restock, HandleRestockAsync);
        return base.OnActivateAsync(cancellationToken);
    }

    public ValueTask<int> GetStock() => new(stock.Value);

    private async ValueTask HandleRestockAsync(Restock message, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        // The commit runs as a grain request, so it waits for the current grain call and excludes others.
        await this.RunRequestAsync(_ =>
        {
            stock.Value = checked(stock.Value + message.Quantity);
            context.Complete();
            return Task.CompletedTask;
        }, cancellationToken);
    }
}
// </messaging_exclusive_commit>

// <messaging_send>
public interface INotificationSenderGrain : IGrainWithStringKey, IMessagingGrain
{
    Task SendAsync(MessageId commandId, GrainId receiver, string message);
}

public sealed class NotificationSenderGrain(
    IOutbox outbox,
    IDurableStateManager stateManager,
    Messages messages,
    [FromKeyedServices("sent-count")] IDurableValue<int> sentCount) : Grain, INotificationSenderGrain
{
    public async Task SendAsync(MessageId commandId, GrainId receiver, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var nextCount = checked(sentCount.Value + 1);
        outbox.Send(messages.Notify, commandId, receiver, new Notify(message));
        sentCount.Value = nextCount;
        await stateManager.WriteStateAsync();
    }
}
// </messaging_send>

// <messaging_shipment>
[GenerateSerializer]
public sealed record Shipment(
    [property: Id(0)] ReserveStock Reservation,
    [property: Id(1)] byte[] Manifest);

public interface IShipmentSenderGrain : IGrainWithStringKey, IMessagingGrain
{
    Task SendAsync(MessageId commandId, GrainId receiver, Shipment shipment);
}

public sealed class ShipmentSenderGrain(
    IOutbox outbox,
    IDurableStateManager state,
    Messages messages)
    : Grain, IShipmentSenderGrain
{
    public async Task SendAsync(MessageId commandId, GrainId receiver, Shipment shipment)
    {
        outbox.Send(messages.Shipment, commandId, receiver, shipment);
        await state.WriteStateAsync();
    }
}
// </messaging_shipment>

// <messaging_byte_payload>
internal static class ShipmentEnvelope
{
    internal static OutboxMessage Create(
        Serializer serializer, MessageId commandId,
        GrainId sender, GrainId receiver, Shipment shipment, Guid traceId)
    {
        var envelope = new Envelope
        {
            MessageId = commandId,
            Subject = "shipments.manifest.v1",
            Payload = serializer.SerializeToArray(shipment),
            Headers = new()
            {
                ["sender"] = serializer.SerializeToArray(sender),
                ["trace-id"] = serializer.SerializeToArray(traceId)
            }
        };
        return new OutboxMessage { Envelope = envelope, ReceiverId = receiver };
    }

    internal static Guid ReadTrace(InboxMessage message, Serializer<Guid> serializer)
    {
        if (message.Envelope.Headers is not { } headers || !headers.TryGetValue("trace-id", out var bytes))
        {
            throw new InvalidOperationException("The shipment requires its trace-id header.");
        }
        return serializer.Deserialize(bytes);
    }
}
// </messaging_byte_payload>
