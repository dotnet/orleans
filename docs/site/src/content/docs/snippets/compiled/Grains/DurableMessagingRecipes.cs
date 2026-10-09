using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;

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

        return HierarchicalKey.Create("tenants", tenantId, "orders", orderId.ToString("N"));
    }

    public static HierarchicalKey Reservation(HierarchicalKey order, string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        return order.CreateChildKey("inventory").CreateChildKey(sku).CreateChildKey("reserve");
    }

    public static HierarchicalKey Charge(HierarchicalKey order) =>
        order.CreateChildKey("payment").CreateChildKey("charge");
}
// </messaging_operation_keys>

// <messaging_hierarchy>
internal static class HierarchyExample
{
    internal static (HierarchicalKey Order, HierarchicalKey Step) Create()
    {
        var order = HierarchicalKey.Create("orders", "42");
        var step = order.CreateChildKey("inventory")
            .CreateChildKey("widget/blue")
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
    [property: Id(1)] int Quantity,
    [property: Id(2)] GrainId ResponseDestination);

[GenerateSerializer]
public sealed record ReservationResult(
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
    private readonly IDurableStateManager _state;
    private readonly IDurableOutbox _outbox;
    private readonly DurableMessageWriter _writer;
    private readonly DurableMessageType<ReserveStock> _reserve;
    private readonly DurableMessageType<ReservationResult> _result;

    public InventoryGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        DurableMessageWriter writer,
        [FromKeyedServices(MessagingSubjects.ReserveStock)] DurableMessageType<ReserveStock> reserve,
        [FromKeyedServices(MessagingSubjects.ReservationResult)] DurableMessageType<ReservationResult> result,
        IDurableStateManager state,
        [FromKeyedServices("available-stock")] IDurableValue<int> available)
    {
        _available = available;
        _state = state;
        _outbox = outbox;
        _writer = writer;
        _reserve = reserve;
        _result = result;
        inbox.RegisterHandler(this);
    }

    public async Task SetAvailableAsync(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        _available.Value = quantity;
        await _state.WriteStateAsync();
    }

    public ValueTask<int> GetAvailableAsync() => new(_available.Value);

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var request = _reserve.Decode(context.Envelope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Quantity);
        var result = new ReservationResult(request.Quantity, _available.Value >= request.Quantity);
        var nextAvailable = result.Reserved
            ? checked(_available.Value - request.Quantity)
            : _available.Value;
        using var reply = _writer.Create(_result, context.Envelope.MessageId.CreateChildKey("result"),
            request.ResponseDestination, result);
        cancellationToken.ThrowIfCancellationRequested();

        _available.Value = nextAvailable;
        _outbox.Send(reply);
        context.Complete();
        return ValueTask.CompletedTask;
    }
}
// </messaging_inventory>

// <messaging_payment>
[GenerateSerializer]
public sealed record ChargePayment(
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
    // The provider binds the canonical command key to its original request and result.
    Task<PaymentResult> ChargeAsync(
        string idempotencyKey, ChargePayment request, CancellationToken cancellationToken);
}

public interface IPaymentGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<PaymentResult?> GetResultAsync(HierarchicalKey commandId);
}

public sealed class PaymentGrain : Grain, IPaymentGrain, IInboxHandler
{
    private readonly IIdempotentPaymentGateway _gateway;
    private readonly IDurableOutbox _outbox;
    private readonly DurableMessageWriter _writer;
    private readonly DurableMessageType<ChargePayment> _charge;
    private readonly DurableMessageType<PaymentResult> _result;
    private readonly IDurableDictionary<HierarchicalKey, PaymentResult> _results;

    public PaymentGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        DurableMessageWriter writer,
        [FromKeyedServices(MessagingSubjects.ChargePayment)] DurableMessageType<ChargePayment> charge,
        [FromKeyedServices(MessagingSubjects.PaymentResult)] DurableMessageType<PaymentResult> result,
        IIdempotentPaymentGateway gateway,
        [FromKeyedServices("payment-results")] IDurableDictionary<HierarchicalKey, PaymentResult> results)
    {
        _gateway = gateway;
        _outbox = outbox;
        _writer = writer;
        _charge = charge;
        _result = result;
        _results = results;
        inbox.RegisterHandler(this);
    }

    public ValueTask<PaymentResult?> GetResultAsync(HierarchicalKey commandId) =>
        new(_results.TryGetValue(commandId, out var result) ? result : null);

    public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var request = _charge.Decode(context.Envelope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Currency);
        if (request.ResponseDestination.IsDefault)
        {
            throw new ArgumentException("A payment requires a response destination.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var commandId = context.Envelope.MessageId;
        var result = await _gateway.ChargeAsync(commandId.ToString(), request, cancellationToken);
        if (result.Request != request || string.IsNullOrWhiteSpace(result.ProviderReference))
        {
            throw new InvalidOperationException("The payment provider returned an inconsistent result.");
        }

        using var reply = _writer.Create(_result, commandId.CreateChildKey("result"),
            request.ResponseDestination, result);
        cancellationToken.ThrowIfCancellationRequested();

        _results[commandId] = result;
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
    private readonly DurableMessageType<StockSnapshot> _type;

    public StockProjectionGrain(
        IDurableInbox inbox,
        [FromKeyedServices(MessagingSubjects.StockSnapshot)] DurableMessageType<StockSnapshot> type,
        [FromKeyedServices("stock-snapshot")] IDurableValue<StockSnapshot> snapshot)
    {
        _snapshot = snapshot;
        _type = type;
        inbox.RegisterHandler(this);
    }

    public ValueTask<StockSnapshot> GetSnapshotAsync() =>
        new(_snapshot.Value ?? new StockSnapshot(0, 0));

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var update = _type.Decode(context.Envelope);
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
    ValueTask<NotificationCampaign?> GetCampaignAsync(Guid campaignId);
}

public sealed class CampaignGrain(
    IDurableOutbox outbox,
    IDurableStateManager state,
    [FromKeyedServices("campaigns")] IDurableDictionary<Guid, NotificationCampaign> campaigns,
    [FromKeyedServices(MessagingSubjects.Notify)] DurableMessageType<Notify> notification,
    DurableMessageWriter writer,
    IGrainContext grainContext) : Grain(grainContext), ICampaignGrain
{
    public ValueTask<NotificationCampaign?> GetCampaignAsync(Guid campaignId) =>
        new(campaigns.TryGetValue(campaignId, out var campaign) ? campaign : null);

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
        var root = HierarchicalKey.Create("campaigns", campaignId.ToString("N"));
        var messages = new List<DurableEnvelope>(campaign.Recipients.Length);
        try
        {
            foreach (var recipient in campaign.Recipients)
            {
                messages.Add(writer.Create(notification, root.CreateChildKey(recipient.ToString()),
                    recipient, new Notify(text)));
            }

            campaigns.Add(campaignId, campaign);
            foreach (var message in messages)
            {
                outbox.Send(message);
            }
        }
        finally
        {
            foreach (var message in messages)
            {
                message.Dispose();
            }
        }
        await state.WriteStateAsync();
    }
}
// </messaging_fanout>

// <messaging_dispatcher>
public interface IOrderOutcomesGrain : IGrainWithStringKey, IDurableMessagingGrain
{
    ValueTask<int> GetCompletedStepCountAsync();
    ValueTask<OrderOutcome?> GetOutcomeAsync(HierarchicalKey replyId);
}

public sealed class OrderOutcomesGrain : Grain, IOrderOutcomesGrain
{
    private readonly IDurableDictionary<HierarchicalKey, OrderOutcome> _outcomes;

    public OrderOutcomesGrain(
        IDurableInbox inbox,
        [FromKeyedServices(MessagingSubjects.ReservationResult)] DurableMessageType<ReservationResult> reservation,
        [FromKeyedServices(MessagingSubjects.PaymentResult)] DurableMessageType<PaymentResult> payment,
        [FromKeyedServices("order-outcomes")] IDurableDictionary<HierarchicalKey, OrderOutcome> outcomes)
    {
        _outcomes = outcomes;
        var dispatcher = new DurableInboxDispatcher()
            .Register(reservation, (result, context, token) => Record(result, context, token))
            .Register(payment, (result, context, token) => Record(result, context, token));
        inbox.RegisterHandler(dispatcher);
    }

    public ValueTask<int> GetCompletedStepCountAsync() => new(_outcomes.Count);

    public ValueTask<OrderOutcome?> GetOutcomeAsync(HierarchicalKey replyId) =>
        new(_outcomes.TryGetValue(replyId, out var outcome) ? outcome : null);

    private ValueTask Record(
        OrderOutcome outcome, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _outcomes[context.Envelope.MessageId] = outcome;
        context.Complete();
        return ValueTask.CompletedTask;
    }
}
// </messaging_dispatcher>
