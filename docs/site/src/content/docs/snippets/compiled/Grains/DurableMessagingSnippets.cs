using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;

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
        MessagingProtocol.Register(siloBuilder.Services);
        // </messaging_registration>
    }
}

// <messaging_payload>
public static class MessagingSubjects
{
    public const string Notify = "notifications.notify.v1";
    public const string NotificationReceived = "notifications.received.v1";
    public const string ReserveStock = "inventory.reserve.v1";
    public const string ReservationResult = "inventory.reservation-result.v1";
    public const string ChargePayment = "payments.charge.v1";
    public const string PaymentResult = "payments.result.v1";
    public const string StockSnapshot = "inventory.snapshot.v1";
}

public static class MessagingProtocol
{
    public static void Register(IServiceCollection services)
    {
        services.AddDurableMessageType<Notify>(MessagingSubjects.Notify);
        services.AddDurableMessageType<NotificationReceived>(MessagingSubjects.NotificationReceived);
        services.AddDurableMessageType<ReserveStock>(MessagingSubjects.ReserveStock);
        services.AddDurableMessageType<ReservationResult>(MessagingSubjects.ReservationResult);
        services.AddDurableMessageType<ChargePayment>(MessagingSubjects.ChargePayment);
        services.AddDurableMessageType<PaymentResult>(MessagingSubjects.PaymentResult);
        services.AddDurableMessageType<StockSnapshot>(MessagingSubjects.StockSnapshot);
    }
}

[GenerateSerializer]
public sealed record Notify(
    [property: Id(0)] string? Text,
    [property: Id(2)] GrainId? ResponseDestination = null);

[GenerateSerializer]
public sealed record NotificationReceived(
    [property: Id(0)] string Text,
    [property: Id(1)] HierarchicalKey CommandId);
// </messaging_payload>

// <messaging_grain>
public interface INotificationGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<int> GetCount();
}

public sealed class NotificationGrain : Grain, INotificationGrain, IInboxHandler
{
    private readonly IDurableOutbox _outbox;
    private readonly DurableMessageWriter _writer;
    private readonly DurableMessageType<Notify> _notification;
    private readonly DurableMessageType<NotificationReceived> _received;
    private readonly IDurableValue<int> _count;

    public NotificationGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        DurableMessageWriter writer,
        [FromKeyedServices(MessagingSubjects.Notify)] DurableMessageType<Notify> notification,
        [FromKeyedServices(MessagingSubjects.NotificationReceived)] DurableMessageType<NotificationReceived> received,
        [FromKeyedServices("notification-count")] IDurableValue<int> count)
    {
        _outbox = outbox;
        _writer = writer;
        _notification = notification;
        _received = received;
        _count = count;
        inbox.RegisterHandler(this);
    }

    public ValueTask<int> GetCount() => new(_count.Value);

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var message = _notification.Decode(context.Envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Text);
        var nextCount = checked(_count.Value + 1);
        using DurableEnvelope? reply = message.ResponseDestination is { } recipient
            ? _writer.Create(_received, context.Envelope.MessageId.CreateChildKey("result"), recipient,
                new NotificationReceived(message.Text, context.Envelope.MessageId))
            : null;
        cancellationToken.ThrowIfCancellationRequested();

        _count.Value = nextCount;
        if (reply is { } envelope)
        {
            _outbox.Send(envelope);
        }
        context.Complete();
        return ValueTask.CompletedTask;
    }
}
// </messaging_grain>

// <messaging_send>
public interface INotificationSenderGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task SendAsync(HierarchicalKey commandId, GrainId receiver, string message);
}

public sealed class NotificationSenderGrain(
    IDurableOutbox outbox,
    IDurableStateManager stateManager,
    [FromKeyedServices("sent-count")] IDurableValue<int> sentCount,
    [FromKeyedServices(MessagingSubjects.Notify)] DurableMessageType<Notify> notification,
    DurableMessageWriter writer) : Grain, INotificationSenderGrain
{
    public async Task SendAsync(HierarchicalKey commandId, GrainId receiver, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        using (var envelope = writer.Create(notification, commandId, receiver, new Notify(message)))
        {
            var nextCount = checked(sentCount.Value + 1);
            sentCount.Value = nextCount;
            outbox.Send(envelope);
        }
        await stateManager.WriteStateAsync();
    }
}
// </messaging_send>

// <messaging_buffer_package>
internal static class ShipmentPackage
{
    public const string Subject = "shipments.manifest.v1";

    internal static DurableEnvelope CreateEnvelope(
        Serializer serializer, ArcBufferWriter encoder, HierarchicalKey commandId,
        GrainId sender, GrainId receiver, ReserveStock request, ReadOnlySpan<byte> manifest) => new()
    {
        MessageId = commandId,
        Subject = Subject,
        SenderId = sender,
        ReceiverId = receiver,
        Payload = Encode(serializer, encoder, request, manifest)
    };

    internal static ArcBuffer Encode(
        Serializer serializer, ArcBufferWriter encoder, ReserveStock request, ReadOnlySpan<byte> manifest)
    {
        using var builder = new BufferPackageBuilder();
        builder.Add("reservation", writer => serializer.Serialize(request, writer));
        builder.Add("manifest", manifest);
        using var package = builder.Build();
        try
        {
            serializer.Serialize(package, encoder);
            return encoder.ConsumeSlice(encoder.Length);
        }
        catch
        {
            encoder.Reset();
            throw;
        }
    }

    internal static BufferPackage DecodeEnvelope(Serializer serializer, DurableEnvelope envelope)
    {
        if (!string.Equals(envelope.Subject, Subject, StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected a shipment manifest subject.", nameof(envelope));
        }
        return Decode(serializer, envelope.Payload);
    }

    internal static BufferPackage Decode(Serializer serializer, ArcBuffer payload) =>
        serializer.Deserialize<BufferPackage>(payload)
        ?? throw new ArgumentException("A shipment requires a package.");

    internal static ReserveStock ReadReservation(Serializer serializer, BufferPackage package)
    {
        if (!package.TryGetBytes("reservation", out var request))
        {
            throw new ArgumentException("A shipment requires a reservation entry.");
        }
        // The entry sequence is borrowed while the package owner stays alive.
        return serializer.Deserialize<ReserveStock>(request)
            ?? throw new ArgumentException("A shipment requires a reservation request.");
    }
}
// </messaging_buffer_package>

// <messaging_arc_ownership>
internal static class ArcPayloadEncoder
{
    internal static DurableEnvelope CreateEnvelope(
        Serializer<Notify> serializer, ArcBufferWriter encoder, HierarchicalKey commandId,
        GrainId sender, GrainId receiver, Notify message) => new()
    {
        MessageId = commandId,
        Subject = MessagingSubjects.Notify,
        SenderId = sender,
        ReceiverId = receiver,
        Payload = Encode(serializer, encoder, message)
    };

    internal static ArcBuffer Encode(Serializer<Notify> serializer, ArcBufferWriter encoder, Notify message)
    {
        try
        {
            serializer.Serialize(message, encoder);
            return encoder.ConsumeSlice(encoder.Length);
        }
        catch
        {
            encoder.Reset();
            throw;
        }
    }

    internal static Notify DecodeRetained(Serializer serializer, ArcBuffer borrowedPayload)
    {
        using var retained = borrowedPayload.Slice(0, borrowedPayload.Length);
        using var session = serializer.SessionPool.GetSession();
        var reader = Reader.Create(retained, session);
        return serializer.GetSerializer<Notify>().Deserialize(ref reader)
            ?? throw new ArgumentException("A notification is required.");
    }
}
// </messaging_arc_ownership>
