using System.Diagnostics;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IOrderGrain : IGrainWithGuidKey
{
    Task<OrderDetails?> GetAsync();

    // The order workflow, which basket checkout schedules under PlaceOrderTaskId(orderId).
    DurableTask<OrderResult> PlaceOrderAsync(SubmitOrder request);

    Task<bool> CancelAsync();

    Task<bool> ShipAsync();
}

// The order process manager, following eShop's ordering flow:
// Submitted -> (grace period) -> AwaitingValidation -> StockConfirmed -> Paid -> Shipped,
// or Cancelled when stock is rejected, payment is declined, or the buyer cancels before stock is confirmed.
// PlaceOrder is one durable task. After a deactivation, the runtime replays it from the start: completed child tasks
// return their recorded results, so the workflow resumes where it stopped. Each state transition is a durable task
// method which the workflow awaits as an identified child, so its state change commits once with its recorded result,
// which a replay returns without running the transition again. Cancel and ship are ordinary methods: cancel requests
// durable cancellation of the workflow with the buyer's reason, and ship sends the workflow its Shipped signal.
public sealed class OrderGrain : DurableGrain, IOrderGrain
{
    private const string CancelledByBuyer = "Cancelled by the buyer.";

    // A paid order's workflow waits for this signal, which ShipAsync sends.
    private static readonly DurableSignal<Shipment> Shipped = new("shipped");

    private readonly OrderingOptions _options;
    private readonly IDurableTaskManager _tasks;
    private readonly IDurableValue<OrderState> _order;
    private readonly IDurableDictionary<int, StockLineStatus> _stock;

    public OrderGrain(IOptions<OrderingOptions> options, IDurableTaskManager tasks)
    {
        _options = options.Value;
        _tasks = tasks;
        _order = StateManager.GetOrAddValue<OrderState>("order");
        _stock = StateManager.GetOrAddDictionary<int, StockLineStatus>("stock");
    }

    private Guid OrderId => this.GetPrimaryKey();

    public static string PlaceOrderTaskId(Guid orderId) => $"place-order-{orderId:N}";

    public Task<OrderDetails?> GetAsync()
    {
        var order = _order.Value;
        var details = order is null
            ? null
            : new OrderDetails(order, _stock.Select(line => new StockLine(line.Key, line.Value)).ToArray());
        return Task.FromResult(details);
    }

    public async DurableTask<OrderResult> PlaceOrderAsync(SubmitOrder request)
    {
        var lines = request.Lines;
        var summary = await Submit(request).WithId("submit");
        await RecordAsync(summary);

        // Until stock is confirmed, CancelAsync can cancel this task. The step which observes the cancellation throws
        // OperationCanceledException, and the workflow releases the lines which validation may have reserved.
        OrderLine[] reservable = [];
        bool[] confirmed;
        try
        {
            await DurableTask.Delay(_options.GracePeriod).WithId("grace-period");
            summary = await BeginValidation().WithId("validate");
            reservable = lines;
            await RecordAsync(summary);

            // Reserves every line concurrently, as the children reserve/0, reserve/1, and so on. WhenAll waits for every
            // reservation, including after a cancellation, so each line's reservation is settled when it returns or throws.
            confirmed = await DurableTask.WhenAll(lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units)))
                .WithId("reserve");
            summary = await RecordStock(lines, confirmed).WithId("record-stock");
        }
        catch (OperationCanceledException) when (DurableExecutionContext.Current!.IsCancellationRequested)
        {
            return await CancelOrderAsync(reservable, DurableExecutionContext.Current.CancellationReason);
        }

        if (Array.IndexOf(confirmed, false) is var rejected and >= 0)
        {
            return await CancelOrderAsync(lines, $"'{lines[rejected].ProductName}' is out of stock.");
        }

        await RecordAsync(summary);

        var payment = await GrainFactory.GetGrain<IPaymentGrain>(OrderId)
            .AuthorizeAsync(lines.Sum(line => line.UnitPrice * line.Units), request.CardLast4)
            .WithId("authorize-payment");
        if (!payment.Succeeded)
        {
            return await CancelOrderAsync(lines, payment.Reason);
        }

        summary = await MarkPaid().WithId("pay");
        await RecordAsync(summary);
        await DurableTask.WhenAll(lines.Select(line => Catalog(line.ProductId).CommitAsync(OrderId))).WithId("commit");

        // The order ships when ShipAsync signals it; the wait returns at once for a shipment which arrived earlier.
        await Shipped.WaitAsync();
        summary = await MarkShipped().WithId("ship");
        await RecordAsync(summary);
        return Result(summary);
    }

    // The workflow observes the cancellation during its grace period or stock validation, releases the stock it
    // reserved, and records the cancellation with this reason.
    public async Task<bool> CancelAsync()
    {
        if (_order.Value?.Status is not (OrderStatus.Submitted or OrderStatus.AwaitingValidation))
        {
            return false;
        }

        await _tasks.CancelAsync(PlaceOrderTaskId(OrderId), CancelledByBuyer);
        return true;
    }

    // The workflow records the first shipment, so shipping again conflicts with it, including before the workflow has
    // recorded the Shipped status.
    public async Task<bool> ShipAsync()
    {
        if (_order.Value?.Status is not OrderStatus.Paid)
        {
            return false;
        }

        var result = await _tasks.TrySignalAsync(PlaceOrderTaskId(OrderId), Shipped, new Shipment(DateTimeOffset.UtcNow));
        return result is DurableSignalResult.Accepted;
    }

    // Releases the lines' reservations concurrently, then records the cancellation. Releasing is idempotent and returns
    // whether the line held a reservation, so the workflow releases each line without tracking which reservations ran.
    // The suppression scope keeps the workflow's cancellation from reaching this compensation, so it runs to completion.
    private async Task<OrderResult> CancelOrderAsync(OrderLine[] lines, string? reason)
    {
        using (DurableExecutionContext.SuppressCancellation())
        {
            var released = await DurableTask.WhenAll(lines.Select(line => Catalog(line.ProductId).ReleaseAsync(OrderId)))
                .WithId("release");
            var releasedIds = lines.Where((_, i) => released[i]).Select(line => line.ProductId).ToArray();
            var summary = await Cancel(releasedIds, reason).WithId("cancel");
            await RecordAsync(summary);
            return Result(summary);
        }
    }

    private async DurableTask<OrderSummary> Submit(SubmitOrder request)
    {
        Debug.Assert(_order.Value is null, "Each order runs one PlaceOrder workflow.");
        _order.Value = new OrderState(
            OrderId,
            request.BuyerId,
            Version: 0,
            OrderStatus.Submitted,
            DateTimeOffset.UtcNow,
            request.Lines,
            request.Address,
            request.CardLast4,
            Description: null);
        return _order.Value.ToSummary();
    }

    private async DurableTask<OrderSummary> BeginValidation()
    {
        Debug.Assert(_order.Value!.Status is OrderStatus.Submitted, "Only the workflow changes the order's status.");
        foreach (var line in _order.Value.Lines)
        {
            _stock[line.ProductId] = StockLineStatus.Pending;
        }

        return SetStatus(OrderStatus.AwaitingValidation);
    }

    private async DurableTask<OrderSummary> RecordStock(OrderLine[] lines, bool[] confirmed)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            _stock[lines[i].ProductId] = confirmed[i] ? StockLineStatus.Confirmed : StockLineStatus.Rejected;
        }

        return confirmed.All(value => value) ? SetStatus(OrderStatus.StockConfirmed) : _order.Value!.ToSummary();
    }

    private async DurableTask<OrderSummary> MarkPaid()
    {
        foreach (var productId in _stock.Keys.ToArray())
        {
            _stock[productId] = StockLineStatus.Committed;
        }

        return SetStatus(OrderStatus.Paid);
    }

    private async DurableTask<OrderSummary> MarkShipped() => SetStatus(OrderStatus.Shipped);

    private async DurableTask<OrderSummary> Cancel(int[] released, string? reason)
    {
        foreach (var productId in released)
        {
            _stock[productId] = StockLineStatus.Released;
        }

        return SetStatus(OrderStatus.Cancelled, reason);
    }

    private OrderSummary SetStatus(OrderStatus status, string? description = null)
    {
        var order = _order.Value!;
        _order.Value = order with { Status = status, Version = order.Version + 1, Description = description };
        return _order.Value.ToSummary();
    }

    // Detaches the buyer's projection update under a root ID derived from the workflow's ID and the version, such as
    // "place-order-{orderId}:buyer-v2", so the workflow doesn't wait for the buyer and its cancellation doesn't reach the
    // update. Updates arrive in any order; the buyer keeps the summary with the highest version.
    private ValueTask RecordAsync(OrderSummary summary) =>
        GrainFactory.GetGrain<IBuyerGrain>(_order.Value!.BuyerId).RecordOrderAsync(summary)
            .DetachAsync(DetachedTaskId.For($"buyer-v{summary.Version}"));

    private ICatalogItemGrain Catalog(int productId) => GrainFactory.GetGrain<ICatalogItemGrain>(productId);

    private static OrderResult Result(OrderSummary summary) => new(summary.Status, summary.Description);
}
