using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;

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

// <messaging_inventory>
[GenerateSerializer]
public sealed record ReserveStock(
    [property: Id(0)] HierarchicalKey OperationKey,
    [property: Id(1)] int Quantity);

[GenerateSerializer]
public sealed record ReservationResult(
    [property: Id(0)] HierarchicalKey OperationKey,
    [property: Id(1)] int Quantity,
    [property: Id(2)] bool Reserved);

public interface IInventoryGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    Task SetAvailableAsync(int quantity);
    ValueTask<int> GetAvailableAsync();
}

public sealed class InventoryGrain : Grain, IInventoryGrain, IInboxHandler<ReserveStock>
{
    private readonly IDurableValue<int> _available;
    private readonly IDurableDictionary<HierarchicalKey, ReservationResult> _reservations;
    private readonly IDurableStateManager _state;

    public InventoryGrain(
        IDurableInbox inbox,
        IDurableStateManager state,
        [FromKeyedServices("available-stock")] IDurableValue<int> available,
        [FromKeyedServices("reservations")] IDurableDictionary<HierarchicalKey, ReservationResult> reservations)
    {
        _available = available;
        _reservations = reservations;
        _state = state;
        inbox.RegisterHandler("inventory/reserve", this);
    }

    public async Task SetAvailableAsync(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        _available.Value = quantity;
        await _state.WriteStateAsync();
    }

    public ValueTask<int> GetAvailableAsync() => new(_available.Value);

    public ValueTask HandleAsync(
        ReserveStock? request, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OperationKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Quantity);
        var replyTo = context.Envelope.ReplyTo
            ?? throw new ArgumentException("A reservation requires a reply destination.");
        if (!request.OperationKey.Equals(context.Envelope.CorrelationKey))
        {
            throw new ArgumentException("The reservation and envelope must identify the same operation.");
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
        var reply = context.CreateEnvelope()
            .To(replyTo, "inventory/reserved")
            .WithCorrelationKey(request.OperationKey)
            .WithBody(result)
            .Build();
        cancellationToken.ThrowIfCancellationRequested();

        if (!alreadyRecorded)
        {
            _available.Value = nextAvailable;
            _reservations.Add(request.OperationKey, result);
        }
        context.Send(reply);
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
    [property: Id(2)] string Currency);

[GenerateSerializer]
public sealed record PaymentResult(
    [property: Id(0)] ChargePayment Request,
    [property: Id(1)] string ProviderReference,
    [property: Id(2)] bool Charged);

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

public sealed class PaymentGrain : Grain, IPaymentGrain, IInboxHandler<ChargePayment>
{
    private readonly IIdempotentPaymentGateway _gateway;
    private readonly IDurableDictionary<HierarchicalKey, PaymentResult> _results;

    public PaymentGrain(
        IDurableInbox inbox,
        IIdempotentPaymentGateway gateway,
        [FromKeyedServices("payment-results")] IDurableDictionary<HierarchicalKey, PaymentResult> results)
    {
        _gateway = gateway;
        _results = results;
        inbox.RegisterHandler("payment/charge", this);
    }

    public ValueTask<PaymentResult?> GetResultAsync(HierarchicalKey operationKey) =>
        new(_results.TryGetValue(operationKey, out var result) ? result : null);

    public async ValueTask HandleAsync(
        ChargePayment? request, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OperationKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Currency);
        var replyTo = context.Envelope.ReplyTo
            ?? throw new ArgumentException("A payment requires a reply destination.");
        if (!request.OperationKey.Equals(context.Envelope.CorrelationKey))
        {
            throw new ArgumentException("The payment and envelope must identify the same operation.");
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

        var reply = context.CreateEnvelope()
            .To(replyTo, "payment/result")
            .WithCorrelationKey(request.OperationKey)
            .WithBody(result)
            .Build();
        cancellationToken.ThrowIfCancellationRequested();

        if (!alreadyRecorded)
        {
            _results.Add(request.OperationKey, result);
        }
        context.Send(reply);
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

public sealed class StockProjectionGrain : Grain, IStockProjectionGrain, IInboxHandler<StockSnapshot>
{
    private readonly IDurableValue<StockSnapshot> _snapshot;

    public StockProjectionGrain(
        IDurableInbox inbox,
        [FromKeyedServices("stock-snapshot")] IDurableValue<StockSnapshot> snapshot)
    {
        _snapshot = snapshot;
        inbox.RegisterHandler("stock/snapshot", this);
    }

    public ValueTask<StockSnapshot> GetSnapshotAsync() =>
        new(_snapshot.Value ?? new StockSnapshot(0, 0));

    public ValueTask HandleAsync(
        StockSnapshot? update, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
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
    SerializerSessionPool sessions) : Grain, ICampaignGrain
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
        var messages = campaign.Recipients.Select(recipient =>
            new DurableEnvelopeBuilder(sessions, this.GetGrainId())
                .To(recipient, "notifications")
                .WithCorrelationKey(root.CreateChildKey(Uri.EscapeDataString(recipient.ToString())))
                .WithBody(text)
                .Build()).ToArray();

        campaigns.Add(campaignId, campaign);
        foreach (var message in messages)
        {
            outbox.Send(message);
        }
        await state.WriteStateAsync();
    }
}
// </messaging_fanout>
