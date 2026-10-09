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
public sealed class ApplicationPayload(Serializer serializer) : IDisposable
{
    // One encoder per non-reentrant activation: consumed slices can share pages.
    private readonly ArcBufferWriter _encoder = new();

    public ArcBuffer Encode(object message)
    {
        try
        {
            serializer.Serialize(message, _encoder);
            return _encoder.ConsumeSlice(_encoder.Length);
        }
        catch
        {
            _encoder.Reset(); // Discard a partially encoded message before reuse.
            throw;
        }
    }

    // Borrow the input: ordinary serialization/deserialization never consumes it.
    public T Decode<T>(ArcBuffer payload) where T : class =>
        serializer.Deserialize<object>(payload) as T
        ?? throw new ArgumentException($"Expected an application message of type {typeof(T).Name}.");

    public DurableEnvelope Envelope(GrainId sender, GrainId receiver, object message) => new()
    {
        MessageId = Guid.NewGuid(),
        SenderId = sender,
        ReceiverId = receiver,
        Payload = Encode(message)
    };

    public void Dispose() => _encoder.Dispose();
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

public sealed class NotificationGrain : Grain, INotificationGrain, IInboxHandler, IDisposable
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

    public void Dispose() => _payload.Dispose();

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
        using DurableEnvelope? reply = message.ResponseDestination is { } recipient
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
    Serializer serializer) : Grain, INotificationSenderGrain, IDisposable
{
    private readonly ApplicationPayload _payload = new(serializer);

    public void Dispose() => _payload.Dispose();

    public async Task SendAsync(GrainId receiver, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (receiver.IsDefault)
        {
            throw new ArgumentException("Specify a nondefault receiver.", nameof(receiver));
        }
        using (var envelope = _payload.Envelope(this.GetGrainId(), receiver, new Notify(message)))
        {
            var nextCount = checked(sentCount.Value + 1);
            sentCount.Value = nextCount;
            outbox.Send(envelope); // Borrows; durable state retains an independent pin.
        }
        await stateManager.WriteStateAsync();
    }
}
// </messaging_send>

// <messaging_buffer_package>
internal static class ShipmentPackage
{
    // The caller supplies its reusable activation- or scope-owned encoder.
    internal static ArcBuffer Encode(
        Serializer serializer, ArcBufferWriter encoder, ReserveStock request, ReadOnlySpan<byte> manifest)
    {
        using var builder = new BufferPackageBuilder();
        builder.Add("reservation", writer => serializer.Serialize(request, writer));
        builder.Add("manifest", manifest);
        using var package = builder.Build(); // Transfers the builder's one buffer owner.
        try
        {
            serializer.Serialize(package, encoder); // Borrows package; does not release it.
            return encoder.ConsumeSlice(encoder.Length); // Caller owns this payload.
        }
        catch
        {
            encoder.Reset();
            throw;
        }
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
        // The entry sequence is borrowed; the caller keeps the package alive.
        return serializer.Deserialize<ReserveStock>(request)
            ?? throw new ArgumentException("A shipment requires a reservation request.");
    }
}
// </messaging_buffer_package>

// <messaging_arc_ownership>
internal static class ArcPayloadEncoder
{
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
        using var retained = borrowedPayload.Slice(0, borrowedPayload.Length); // Independent pin.
        // Reader.Create(ArcBuffer, session) preserves Arc-backed decode, including retained sub-slices.
        using var session = serializer.SessionPool.GetSession();
        var reader = Reader.Create(retained, session);
        return serializer.GetSerializer<Notify>().Deserialize(ref reader)
            ?? throw new ArgumentException("A notification is required.");
    }
}
// </messaging_arc_ownership>
