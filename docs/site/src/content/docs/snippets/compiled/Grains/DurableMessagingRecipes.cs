using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableMessaging;

// <messaging_operation_keys>
public static class OrderOperationKeys
{
    public static HierarchicalKey Order(string tenantId, Guid orderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("An order ID must be nonempty.", nameof(orderId));
        }

        return HierarchicalKey.Create(
            "tenants", Uri.EscapeDataString(tenantId), "orders", orderId.ToString("N"));
    }

    public static HierarchicalKey Reservation(HierarchicalKey order, string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        return order.CreateChildKey("inventory")
            .CreateChildKey(Uri.EscapeDataString(sku))
            .CreateChildKey("reserve");
    }

    public static HierarchicalKey Charge(HierarchicalKey order) =>
        order.CreateChildKey("payment/charge");
}
// </messaging_operation_keys>

// <messaging_hierarchy>
internal static class HierarchyExample
{
    internal static (HierarchicalKey Order, HierarchicalKey Step) Create()
    {
        var order = HierarchicalKey.Create("orders/42");
        var step = order.CreateChildKey("inventory")
            .CreateEscapedChildKey("widget/blue")
            .CreateChildKey("reserve");

        // orders/42/inventory/widget\/blue/reserve
        return (order, step);
    }
}
// </messaging_hierarchy>

[GenerateSerializer]
public abstract record OrderOutcome;

// <messaging_inventory>
[GenerateSerializer]
public sealed record ReserveStock(
    [property: Id(0)] HierarchicalKey OperationKey,
    [property: Id(1)] int Quantity,
    [property: Id(2)] GrainId ResponseDestination);

[GenerateSerializer]
public sealed record ReservationResult(
    [property: Id(0)] HierarchicalKey OperationKey,
    [property: Id(1)] int Quantity,
    [property: Id(2)] bool Reserved) : OrderOutcome;

public interface IInventoryGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task SetAvailableAsync(int quantity);
    ValueTask<int> GetAvailableAsync();
}

public sealed class InventoryGrain : Grain, IInventoryGrain, IInboxHandler
{
    private readonly IDurableValue<int> _available;
    private readonly IDurableDictionary<HierarchicalKey, ReservationResult> _reservations;
    private readonly IDurableStateManager _state;
    private readonly IDurableOutbox _outbox;
    private readonly ApplicationPayload _payload;

    public InventoryGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        Serializer serializer,
        IDurableStateManager state,
        [FromKeyedServices("available-stock")] IDurableValue<int> available,
        [FromKeyedServices("reservations")] IDurableDictionary<HierarchicalKey, ReservationResult> reservations)
    {
        _available = available;
        _reservations = reservations;
        _state = state;
        _outbox = outbox;
        _payload = new ApplicationPayload(serializer);
        inbox.RegisterHandler(this);
    }

    public async Task SetAvailableAsync(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        _available.Value = quantity;
        await _state.WriteStateAsync();
    }

    public ValueTask<int> GetAvailableAsync() => new(_available.Value);

    public ValueTask HandleAsync(
        IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var request = _payload.Decode<ReserveStock>(context.Envelope.Payload);
        ArgumentNullException.ThrowIfNull(request.OperationKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Quantity);
        if (request.ResponseDestination.IsDefault)
        {
            throw new ArgumentException("A reservation requires a response destination.");
        }

        var alreadyRecorded = _reservations.TryGetValue(request.OperationKey, out var result);
        if (alreadyRecorded && result!.Quantity != request.Quantity)
        {
            throw new ArgumentException("An operation key must retain its original quantity.");
        }

        result ??= new ReservationResult(
            request.OperationKey, request.Quantity, _available.Value >= request.Quantity);
        var nextAvailable = alreadyRecorded || !result.Reserved
            ? _available.Value
            : checked(_available.Value - request.Quantity);
        var reply = _payload.Envelope(context.Envelope.ReceiverId, request.ResponseDestination, result);
        cancellationToken.ThrowIfCancellationRequested();

        if (!alreadyRecorded)
        {
            _available.Value = nextAvailable;
            _reservations.Add(request.OperationKey, result);
        }
        _outbox.Send(reply);
        context.Complete();
        return ValueTask.CompletedTask;
    }
}
// </messaging_inventory>

// <messaging_payment>
[GenerateSerializer]
public sealed record ChargePayment(
    [property: Id(0)] HierarchicalKey OperationKey,
    [property: Id(1)] decimal Amount,
    [property: Id(2)] string Currency,
    [property: Id(3)] GrainId ResponseDestination);

[GenerateSerializer]
public sealed record PaymentResult(
    [property: Id(0)] ChargePayment Request,
    [property: Id(1)] string ProviderReference,
    [property: Id(2)] bool Charged) : OrderOutcome;

public interface IIdempotentPaymentGateway
{
    // The provider binds the key to the exact request and returns its original result on retries.
    Task<PaymentResult> ChargeAsync(
        string idempotencyKey, ChargePayment request, CancellationToken cancellationToken);
}

public interface IPaymentGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<PaymentResult?> GetResultAsync(HierarchicalKey operationKey);
}

public sealed class PaymentGrain : Grain, IPaymentGrain, IInboxHandler
{
    private readonly IIdempotentPaymentGateway _gateway;
    private readonly IDurableOutbox _outbox;
    private readonly ApplicationPayload _payload;
    private readonly IDurableDictionary<HierarchicalKey, PaymentResult> _results;

    public PaymentGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        Serializer serializer,
        IIdempotentPaymentGateway gateway,
        [FromKeyedServices("payment-results")] IDurableDictionary<HierarchicalKey, PaymentResult> results)
    {
        _gateway = gateway;
        _outbox = outbox;
        _payload = new ApplicationPayload(serializer);
        _results = results;
        inbox.RegisterHandler(this);
    }

    public ValueTask<PaymentResult?> GetResultAsync(HierarchicalKey operationKey) =>
        new(_results.TryGetValue(operationKey, out var result) ? result : null);

    public async ValueTask HandleAsync(
        IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var request = _payload.Decode<ChargePayment>(context.Envelope.Payload);
        ArgumentNullException.ThrowIfNull(request.OperationKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Currency);
        if (request.ResponseDestination.IsDefault)
        {
            throw new ArgumentException("A payment requires a response destination.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var alreadyRecorded = _results.TryGetValue(request.OperationKey, out var result);
        if (alreadyRecorded && result!.Request != request)
        {
            throw new ArgumentException("An operation key must retain its original payment request.");
        }

        result ??= await _gateway.ChargeAsync(
            request.OperationKey.ToString(), request, cancellationToken);
        if (result.Request != request || string.IsNullOrWhiteSpace(result.ProviderReference))
        {
            throw new InvalidOperationException("The payment provider returned an inconsistent result.");
        }

        var reply = _payload.Envelope(context.Envelope.ReceiverId, request.ResponseDestination, result);
        cancellationToken.ThrowIfCancellationRequested();

        if (!alreadyRecorded)
        {
            _results.Add(request.OperationKey, result);
        }
        _outbox.Send(reply);
        context.Complete();
    }
}
// </messaging_payment>

// <messaging_projection>
[GenerateSerializer]
public sealed record StockSnapshot(
    [property: Id(0)] long Version,
    [property: Id(1)] int Available);

public interface IStockProjectionGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<StockSnapshot> GetSnapshotAsync();
}

public sealed class StockProjectionGrain : Grain, IStockProjectionGrain, IInboxHandler
{
    private readonly IDurableValue<StockSnapshot> _snapshot;
    private readonly ApplicationPayload _payload;

    public StockProjectionGrain(
        IDurableInbox inbox,
        Serializer serializer,
        [FromKeyedServices("stock-snapshot")] IDurableValue<StockSnapshot> snapshot)
    {
        _snapshot = snapshot;
        _payload = new ApplicationPayload(serializer);
        inbox.RegisterHandler(this);
    }

    public ValueTask<StockSnapshot> GetSnapshotAsync() =>
        new(_snapshot.Value ?? new StockSnapshot(0, 0));

    public ValueTask HandleAsync(
        IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var update = _payload.Decode<StockSnapshot>(context.Envelope.Payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(update.Version);
        ArgumentOutOfRangeException.ThrowIfNegative(update.Available);
        var current = _snapshot.Value;
        if (current is not null && update.Version == current.Version && update != current)
        {
            throw new ArgumentException("A snapshot version must retain its original value.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (current is null || update.Version > current.Version)
        {
            _snapshot.Value = update;
        }
        context.Complete();
        return ValueTask.CompletedTask;
    }
}
// </messaging_projection>

// <messaging_fanout>
[GenerateSerializer]
public sealed record NotificationCampaign(
    [property: Id(0)] string Text,
    [property: Id(1)] GrainId[] Recipients);

public interface ICampaignGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task PublishAsync(Guid campaignId, string text, GrainId[] recipients);
}

public sealed class CampaignGrain(
    IDurableOutbox outbox,
    IDurableStateManager state,
    [FromKeyedServices("campaigns")] IDurableDictionary<Guid, NotificationCampaign> campaigns,
    Serializer serializer,
    IGrainContext grainContext) : Grain(grainContext), ICampaignGrain
{
    public async Task PublishAsync(Guid campaignId, string text, GrainId[] recipients)
    {
        if (campaignId == Guid.Empty)
        {
            throw new ArgumentException("A campaign ID must be nonempty.", nameof(campaignId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Length == 0 || recipients.Any(id => id.IsDefault)
            || recipients.Distinct().Count() != recipients.Length)
        {
            throw new ArgumentException("Specify distinct, nondefault recipients.", nameof(recipients));
        }

        if (campaigns.TryGetValue(campaignId, out var previous))
        {
            if (previous.Text != text || !previous.Recipients.SequenceEqual(recipients))
            {
                throw new ArgumentException("A campaign ID must retain its original content and recipients.");
            }
            await state.WriteStateAsync();
            return;
        }

        var campaign = new NotificationCampaign(text, recipients.ToArray());
        var root = HierarchicalKey.Create("campaigns").CreateChildKey(campaignId.ToString("N"));
        var payload = new ApplicationPayload(serializer);
        var messages = campaign.Recipients.Select(recipient =>
            payload.Envelope(this.GetGrainId(), recipient,
                new Notify(text, root.CreateChildKey(Uri.EscapeDataString(recipient.ToString()))))).ToArray();

        campaigns.Add(campaignId, campaign);
        foreach (var message in messages)
        {
            outbox.Send(message);
        }
        await state.WriteStateAsync();
    }
}
// </messaging_fanout>

// <messaging_dispatcher>
public interface IOrderOutcomesGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<int> GetCompletedStepCountAsync();
}

// One registered application dispatcher handles both response kinds.
public sealed class OrderOutcomesGrain : Grain, IOrderOutcomesGrain, IInboxHandler
{
    private readonly ApplicationPayload _payload;
    private readonly IDurableDictionary<HierarchicalKey, OrderOutcome> _outcomes;

    public OrderOutcomesGrain(
        IDurableInbox inbox,
        Serializer serializer,
        [FromKeyedServices("order-outcomes")] IDurableDictionary<HierarchicalKey, OrderOutcome> outcomes)
    {
        _payload = new ApplicationPayload(serializer);
        _outcomes = outcomes;
        inbox.RegisterHandler(this);
    }

    public ValueTask<int> GetCompletedStepCountAsync() => new(_outcomes.Count);

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var outcome = _payload.Decode<OrderOutcome>(context.Envelope.Payload);
        var key = outcome switch
        {
            ReservationResult reservation => reservation.OperationKey,
            PaymentResult payment => payment.Request.OperationKey,
            _ => throw new ArgumentException("Unknown order response kind.")
        };
        ArgumentNullException.ThrowIfNull(key);
        var recorded = _outcomes.TryGetValue(key, out var original);
        if (recorded && original != outcome)
        {
            throw new ArgumentException("An operation key must retain its original outcome.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (!recorded)
        {
            _outcomes.Add(key, outcome);
        }
        context.Complete();
        return ValueTask.CompletedTask;
    }
}
// </messaging_dispatcher>
