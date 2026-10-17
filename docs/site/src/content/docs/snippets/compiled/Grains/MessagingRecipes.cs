using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Messaging;
using Orleans.Journaling;
using Orleans.Runtime;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.Messaging;

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

    public static MessageId Reservation(HierarchicalKey order, string sku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        return new MessageId(order.CreateChildKey("inventory").CreateChildKey(sku).CreateChildKey("reserve"));
    }

    public static MessageId Charge(HierarchicalKey order) =>
        new MessageId(order.CreateChildKey("payment").CreateChildKey("charge"));
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
public sealed record Restock([property: Id(0)] int Quantity);

[GenerateSerializer]
public abstract record ReservationResult(
    [property: Id(1)] int Quantity,
    [property: Id(3)] int RemainingStock) : OrderOutcome;

[GenerateSerializer]
public sealed record ReservationAccepted(int Quantity, int RemainingStock)
    : ReservationResult(Quantity, RemainingStock);

[GenerateSerializer]
public sealed record ReservationRejected(
    int Quantity, int RemainingStock,
    [property: Id(0)] ReservationRejectionReason Reason)
    : ReservationResult(Quantity, RemainingStock);

public enum ReservationRejectionReason
{
    InvalidQuantity,
    InsufficientStock
}

public interface IInventoryGrain : IGrainWithStringKey, IMessagingGrain
{
    Task SetAvailableAsync(int quantity);
    ValueTask<int> GetAvailableAsync();
}

public sealed class InventoryGrain(
    IInbox inbox,
    IOutbox outbox,
    Messages messages,
    IDurableStateManager state,
    [FromKeyedServices("available-stock")] IDurableValue<int> available)
    : Grain, IInventoryGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox
            .Register(messages.ReserveStock, HandleReserveStock)
            .Register(messages.Restock, HandleRestock);
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task SetAvailableAsync(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        available.Value = quantity;
        await state.WriteStateAsync();
    }

    public ValueTask<int> GetAvailableAsync() => new(available.Value);

    private void HandleReserveStock(ReserveStock request, IInboxHandlerContext context)
    {
        var current = available.Value;
        ReservationResult outcome = request.Quantity switch
        {
            <= 0 => new ReservationRejected(request.Quantity, current, ReservationRejectionReason.InvalidQuantity),
            var quantity when quantity > current => new ReservationRejected(quantity, current, ReservationRejectionReason.InsufficientStock),
            var quantity => new ReservationAccepted(quantity, current - quantity)
        };

        outbox.SendReply(messages.ReservationResult, context, request.ResponseDestination, outcome);
        if (outcome is ReservationAccepted) available.Value = outcome.RemainingStock;
        context.Complete();
    }

    private void HandleRestock(Restock request, IInboxHandlerContext context)
    {
        if (request.Quantity <= 0)
        {
            context.Fail("Restock quantity must be positive.");
            return;
        }
        var next = checked(available.Value + request.Quantity);
        available.Value = next;
        context.Complete();
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

public interface IPaymentGrain : IGrainWithStringKey, IMessagingGrain
{
    ValueTask<PaymentResult?> GetResultAsync(MessageId commandId);
}

public sealed class PaymentGrain(
    IInbox inbox,
    IOutbox outbox,
    Messages messages,
    IIdempotentPaymentGateway gateway,
    [FromKeyedServices("payment-results")] IDurableDictionary<MessageId, PaymentResult> results)
    : Grain, IPaymentGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.Register(messages.ChargePayment, HandleChargeAsync);
        return base.OnActivateAsync(cancellationToken);
    }

    public ValueTask<PaymentResult?> GetResultAsync(MessageId commandId)
    {
        results.TryGetValue(commandId, out var outcome);
        return new(outcome);
    }

    private async ValueTask HandleChargeAsync(
        ChargePayment request, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Currency);
        if (request.ResponseDestination.IsDefault)
        {
            throw new ArgumentException("A payment requires a response destination.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var commandId = context.Message.Envelope.MessageId;
        var outcome = await gateway.ChargeAsync(commandId.ToString(), request, cancellationToken);
        if (outcome.Request != request || string.IsNullOrWhiteSpace(outcome.ProviderReference))
        {
            throw new InvalidOperationException("The payment provider returned an inconsistent result.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        outbox.SendReply(messages.PaymentResult, context, request.ResponseDestination, outcome);
        results[commandId] = outcome;
        context.Complete();
    }
}
// </messaging_payment>

// <messaging_projection>
[GenerateSerializer]
public sealed record StockSnapshot(
    [property: Id(0)] long Version,
    [property: Id(1)] int Available);

public interface IStockProjectionGrain : IGrainWithStringKey, IMessagingGrain
{
    ValueTask<StockSnapshot> GetSnapshotAsync();
}

public sealed class StockProjectionGrain(
    IInbox inbox,
    Messages messages,
    [FromKeyedServices("stock-snapshot")] IDurableValue<StockSnapshot> snapshot)
    : Grain, IStockProjectionGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox.Register(messages.StockSnapshot, HandleSnapshot);
        return base.OnActivateAsync(cancellationToken);
    }

    public ValueTask<StockSnapshot> GetSnapshotAsync() =>
        new(snapshot.Value ?? new StockSnapshot(0, 0));

    private void HandleSnapshot(StockSnapshot update, IInboxHandlerContext context)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(update.Version);
        ArgumentOutOfRangeException.ThrowIfNegative(update.Available);
        var current = snapshot.Value;
        if (current is not null && update.Version == current.Version && update != current)
        {
            throw new ArgumentException("A snapshot version must retain its original value.");
        }
        if (current is null || update.Version > current.Version)
        {
            snapshot.Value = update;
        }
        context.Complete();
    }
}
// </messaging_projection>

// <messaging_fanout>
[GenerateSerializer]
public sealed record NotificationCampaign(
    [property: Id(0)] string Text,
    [property: Id(1)] GrainId[] Recipients);

public interface ICampaignGrain : IGrainWithStringKey, IMessagingGrain
{
    Task PublishAsync(Guid campaignId, string text, GrainId[] recipients);
    ValueTask<NotificationCampaign?> GetCampaignAsync(Guid campaignId);
}

public sealed class CampaignGrain(
    IOutbox outbox,
    IDurableStateManager state,
    [FromKeyedServices("campaigns")] IDurableDictionary<Guid, NotificationCampaign> campaigns,
    Messages messages,
    IGrainContext grainContext) : Grain(grainContext), ICampaignGrain
{
    public ValueTask<NotificationCampaign?> GetCampaignAsync(Guid campaignId)
    {
        campaigns.TryGetValue(campaignId, out var campaign);
        return new(campaign);
    }

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
        var message = new Notify(text);
        outbox.Send(messages.Notify, campaign.Recipients.Select(recipient =>
            (new MessageId(root.CreateChildKey(recipient.ToString())), recipient, message)));
        campaigns.Add(campaignId, campaign);
        await state.WriteStateAsync();
    }
}
// </messaging_fanout>

// <messaging_dispatcher>
public interface IOrderOutcomesGrain : IGrainWithStringKey, IMessagingGrain
{
    ValueTask<int> GetCompletedStepCountAsync();
    ValueTask<OrderOutcome?> GetOutcomeAsync(MessageId replyId);
}

public sealed class OrderOutcomesGrain(
    IInbox inbox,
    Messages messages,
    [FromKeyedServices("order-outcomes")] IDurableDictionary<MessageId, OrderOutcome> outcomes)
    : Grain, IOrderOutcomesGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox
            .Register(messages.ReservationResult, Record)
            .Register(messages.PaymentResult, Record);
        return base.OnActivateAsync(cancellationToken);
    }

    public ValueTask<int> GetCompletedStepCountAsync() => new(outcomes.Count);

    public ValueTask<OrderOutcome?> GetOutcomeAsync(MessageId replyId)
    {
        outcomes.TryGetValue(replyId, out var outcome);
        return new(outcome);
    }

    private void Record(OrderOutcome outcome, IInboxHandlerContext context)
    {
        outcomes[context.Message.Envelope.MessageId] = outcome;
        context.Complete();
    }
}
// </messaging_dispatcher>

// <messaging_grace_period>
[GenerateSerializer]
public sealed record SubmitCheckout([property: Id(0)] decimal Total);

[GenerateSerializer, Immutable]
public sealed record GracePeriodElapsed;

public enum CheckoutStatus { None, Submitted, Cancelled, Confirmed }

public interface ICheckoutGrain : IGrainWithStringKey, IMessagingGrain
{
    Task CancelAsync();
    ValueTask<CheckoutStatus> GetStatusAsync();
}

public sealed class CheckoutGrain(
    IInbox inbox,
    IOutbox outbox,
    IDurableStateManager state,
    Messages messages,
    [FromKeyedServices("checkout-status")] IDurableValue<CheckoutStatus> status,
    IGrainContext grainContext) : Grain(grainContext), ICheckoutGrain
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(1);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        inbox
            .Register(messages.SubmitCheckout, Submit)
            .Register(messages.GracePeriodElapsed, Confirm);
        return base.OnActivateAsync(cancellationToken);
    }

    public ValueTask<CheckoutStatus> GetStatusAsync() => new(status.Value);

    public async Task CancelAsync()
    {
        if (status.Value == CheckoutStatus.Submitted)
        {
            status.Value = CheckoutStatus.Cancelled;
        }
        await state.WriteStateAsync();
    }

    private void Submit(SubmitCheckout checkout, IInboxHandlerContext context)
    {
        if (status.Value == CheckoutStatus.None)
        {
            var timerId = context.Message.Envelope.MessageId.Key.CreateChildKey("grace-period");
            outbox.Send(messages.GracePeriodElapsed, new MessageId(timerId), this,
                new GracePeriodElapsed(), DateTimeOffset.UtcNow + GracePeriod);
            status.Value = CheckoutStatus.Submitted;
        }
        context.Complete();
    }

    private void Confirm(GracePeriodElapsed elapsed, IInboxHandlerContext context)
    {
        if (status.Value == CheckoutStatus.Submitted)
        {
            status.Value = CheckoutStatus.Confirmed;
        }
        context.Complete();
    }
}
// </messaging_grace_period>
