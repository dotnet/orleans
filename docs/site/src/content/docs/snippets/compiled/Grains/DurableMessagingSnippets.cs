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
        // </messaging_registration>
    }
}

// <messaging_payload>
// The application codec chooses encoded message kinds using concrete record types.
public sealed class ApplicationPayload(Serializer serializer)
{
    public ImmutableBuffer Encode(object message) =>
        ImmutableBuffer.Create(writer => serializer.Serialize(message, writer));

    public T Decode<T>(ImmutableBuffer payload) where T : class =>
        serializer.Deserialize<object>(payload.Memory) as T
        ?? throw new ArgumentException($"Expected an application message of type {typeof(T).Name}.");

    public DurableEnvelope Envelope(GrainId sender, GrainId receiver, object message) => new()
    {
        MessageId = Guid.NewGuid(),
        SenderId = sender,
        ReceiverId = receiver,
        Payload = Encode(message)
    };
}

[GenerateSerializer]
public sealed record Notify(
    [property: Id(0)] string? Text,
    [property: Id(1)] HierarchicalKey? OperationKey = null,
    [property: Id(2)] GrainId? ResponseDestination = null);

[GenerateSerializer]
public sealed record NotificationReceived(
    [property: Id(0)] string Text,
    [property: Id(1)] HierarchicalKey? OperationKey);
// </messaging_payload>

// <messaging_grain>
public interface INotificationGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<int> GetCount();
}

public sealed class NotificationGrain : Grain, INotificationGrain, IInboxHandler
{
    private readonly IDurableOutbox _outbox;
    private readonly ApplicationPayload _payload;
    private readonly IDurableValue<int> _count;
    private readonly IDurableDictionary<HierarchicalKey, string> _notifications;

    public NotificationGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        Serializer serializer,
        [FromKeyedServices("notification-count")] IDurableValue<int> count,
        [FromKeyedServices("notification-operations")] IDurableDictionary<HierarchicalKey, string> notifications)
    {
        _outbox = outbox;
        _payload = new ApplicationPayload(serializer);
        _count = count;
        _notifications = notifications;
        inbox.RegisterHandler(this);
    }

    public ValueTask<int> GetCount() => new(_count.Value);

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = _payload.Decode<Notify>(context.Envelope.Payload);
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            throw new ArgumentException("A notification must contain a nonempty string.");
        }
        if (message.ResponseDestination is { IsDefault: true })
        {
            throw new ArgumentException("A response destination must be nondefault.");
        }

        var alreadyRecorded = message.OperationKey is { } key
            && _notifications.ContainsKey(key);
        if (alreadyRecorded && _notifications[message.OperationKey!] != message.Text)
        {
            throw new ArgumentException("An operation key must retain its original notification text.");
        }
        var nextCount = alreadyRecorded ? _count.Value : checked(_count.Value + 1);
        DurableEnvelope? reply = message.ResponseDestination is { } recipient
            ? _payload.Envelope(context.Envelope.ReceiverId, recipient,
                new NotificationReceived(message.Text, message.OperationKey))
            : null;
        cancellationToken.ThrowIfCancellationRequested();

        // No awaits from the first shared mutation through method return.
        if (!alreadyRecorded)
        {
            _count.Value = nextCount;
            if (message.OperationKey is { } operationKey)
            {
                _notifications.Add(operationKey, message.Text);
            }
        }
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
    Task SendAsync(GrainId receiver, string message);
}

public sealed class NotificationSenderGrain(
    IDurableOutbox outbox,
    IDurableStateManager stateManager,
    [FromKeyedServices("sent-count")] IDurableValue<int> sentCount,
    Serializer serializer) : Grain, INotificationSenderGrain
{
    public async Task SendAsync(GrainId receiver, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (receiver.IsDefault)
        {
            throw new ArgumentException("Specify a nondefault receiver.", nameof(receiver));
        }
        var envelope = new ApplicationPayload(serializer).Envelope(
            this.GetGrainId(), receiver, new Notify(message));
        var nextCount = checked(sentCount.Value + 1);
        sentCount.Value = nextCount;
        outbox.Send(envelope);
        await stateManager.WriteStateAsync();
    }
}
// </messaging_send>

// <messaging_buffer_package>
internal static class ShipmentPackage
{
    internal static ImmutableBuffer Encode(Serializer serializer, ReserveStock request, byte[] manifest)
    {
        var builder = new BufferPackageBuilder();
        builder.Add("reservation", writer => serializer.Serialize(request, writer));
        builder.Add("manifest", manifest.AsSpan());
        // The package is an ordinary application value encoded into the opaque payload.
        return ImmutableBuffer.Create(writer => serializer.Serialize(builder.Build(), writer));
    }

    internal static (ReserveStock Request, ReadOnlyMemory<byte> Manifest) Decode(
        Serializer serializer, ImmutableBuffer payload)
    {
        var package = serializer.Deserialize<BufferPackage>(payload.Memory)
            ?? throw new ArgumentException("A shipment requires a package.");
        if (!package.TryGetBytes("reservation", out var request)
            || !package.TryGetBytes("manifest", out var manifest))
        {
            throw new ArgumentException("A shipment requires reservation and manifest entries.");
        }
        // Each entry has its own encoding; inspect package.Keys without decoding other entries.
        var reservation = serializer.Deserialize<ReserveStock>(request)
            ?? throw new ArgumentException("A shipment requires a reservation request.");
        return (reservation, manifest);
    }
}
// </messaging_buffer_package>

// <messaging_arc_snapshot>
internal static class ArcPayloadSnapshot
{
    internal static ImmutableBuffer Encode(Serializer<Notify> serializer, Notify message)
    {
        using var writer = new ArcBufferWriter();
        serializer.Serialize(message, writer);
        using var bytes = writer.PeekSlice(writer.Length);
        // Copy once: both Arc owners are released as this helper returns.
        return new ImmutableBuffer(bytes);
    }
}
// </messaging_arc_snapshot>
