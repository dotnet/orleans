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

    // Releases the stock which an order cancelled during validation reserved. The workflow detaches it, under
    // PlaceOrderTaskId(orderId) + "-compensate", so it runs to completion after the cancellation.
    DurableTask ReleaseReservationsAsync(int[] productIds);
}

// The order process manager, following eShop's ordering flow:
// Submitted -> (grace period) -> AwaitingValidation -> StockConfirmed -> Paid -> Shipped,
// or Cancelled when stock is rejected, payment is declined, or the buyer cancels before stock is confirmed.
// PlaceOrder is one durable task. After a deactivation, the runtime replays it from the start: completed child tasks
// return their recorded results, so the workflow resumes where it stopped. Each state transition is a durable task
// method which the workflow awaits as an identified child, so its state change commits once with its recorded result,
// which a replay returns without running the transition again. Cancel and ship are ordinary methods, so the order
// retains no task for either command. Cancellation is cooperative: the workflow observes it, and releases the stock it
// reserved through a detached task.
public sealed class OrderGrain : DurableGrain, IOrderGrain
{
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
        var summary = await Submit(request).WithId("submit");
        await RecordAsync(request.BuyerId, summary);

        // The grace period gives the buyer time to cancel before stock is reserved. Cancelling this task cancels the
        // delay, which ends the workflow.
        await DurableTask.Delay(_options.GracePeriod).WithId("grace-period");
        if (await BeginValidation().WithId("validate") is not { } validating)
        {
            // The buyer cancelled after the delay completed, before the workflow resumed.
            return Result(_order.Value!.ToSummary());
        }

        await RecordAsync(request.BuyerId, validating);

        // Reserve each line's stock concurrently, and await every outcome, so a cancellation knows what was reserved.
        var lines = request.Lines;
        var reservations = await Task.WhenAll(lines.Select(line =>
            Catalog(line.ProductId).ReserveAsync(OrderId, line.Units).WithId($"reserve-{line.ProductId}").ScheduleAsync()));
        var outcomes = await Task.WhenAll(reservations.Select(reservation => reservation.GetResponseAsync()));
        var reserved = lines.Where((_, i) => outcomes[i].Status is DurableTaskStatus.CompletedSuccessfully && outcomes[i].GetResult<bool>())
            .Select(line => line.ProductId)
            .ToArray();
        try
        {
            var confirmed = Array.ConvertAll(outcomes, outcome => outcome.GetResult<bool>());
            if (Array.IndexOf(confirmed, false) is var rejected and >= 0)
            {
                await RecordStock(lines, confirmed).WithId("reject-stock");
                return await CompensateAsync(request.BuyerId, reserved, $"'{lines[rejected].ProductName}' is out of stock.");
            }

            summary = await RecordStock(lines, confirmed).WithId("confirm-stock");
        }
        catch (OperationCanceledException) when (_order.Value!.Status is OrderStatus.Cancelled)
        {
            // The buyer cancelled during validation. The workflow's further steps are canceled, so a task detached on this
            // order releases the reserved stock; cancellation doesn't reach it.
            await GrainFactory.GetGrain<IOrderGrain>(OrderId).ReleaseReservationsAsync(reserved)
                .DetachAsync($"{PlaceOrderTaskId(OrderId)}-compensate");
            return Result(_order.Value!.ToSummary());
        }

        await RecordAsync(request.BuyerId, summary);

        var payment = await GrainFactory.GetGrain<IPaymentGrain>(OrderId)
            .AuthorizeAsync(lines.Sum(line => line.UnitPrice * line.Units), request.CardLast4)
            .WithId("authorize-payment");
        if (!payment.Succeeded)
        {
            return await CompensateAsync(request.BuyerId, reserved, payment.Reason);
        }

        summary = await MarkPaid().WithId("pay");
        await RecordAsync(request.BuyerId, summary);
        await DurableTask.WhenAll(reserved.Select(productId => Catalog(productId).CommitAsync(OrderId)).ToArray());
        return Result(summary);
    }

    // The order records the buyer's decision and detaches the projection update, which commit in one journal write.
    public async Task<bool> CancelAsync()
    {
        if (_order.Value?.Status is not (OrderStatus.Submitted or OrderStatus.AwaitingValidation))
        {
            return false;
        }

        await DetachRecordAsync(SetStatus(OrderStatus.Cancelled, "Cancelled by the buyer."));

        // Durable cancellation ends the workflow's grace period, or its validation, whose reservations it then releases.
        await _tasks.CancelAsync(TaskId.CreateRoot(PlaceOrderTaskId(OrderId)));
        return true;
    }

    public async Task<bool> ShipAsync()
    {
        if (_order.Value?.Status is not OrderStatus.Paid)
        {
            return false;
        }

        await DetachRecordAsync(SetStatus(OrderStatus.Shipped));
        return true;
    }

    // Releases reserved stock in reverse order of reservation, then cancels the order.
    private async Task<OrderResult> CompensateAsync(string buyerId, int[] reserved, string? reason)
    {
        await ReleaseStockAsync(reserved);
        var summary = await Cancel(reserved, reason).WithId("cancel");
        await RecordAsync(buyerId, summary);
        return Result(summary);
    }

    public async DurableTask ReleaseReservationsAsync(int[] productIds)
    {
        await ReleaseStockAsync(productIds);
        await MarkReleased(productIds).WithId("mark-released");
    }

    private async Task ReleaseStockAsync(int[] reserved)
    {
        for (var i = reserved.Length - 1; i >= 0; i--)
        {
            await Catalog(reserved[i]).ReleaseAsync(OrderId).WithId($"release-{reserved[i]}");
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

    private async DurableTask<OrderSummary?> BeginValidation()
    {
        var order = _order.Value!;
        if (order.Status is not OrderStatus.Submitted)
        {
            return null;
        }

        foreach (var line in order.Lines)
        {
            _stock[line.ProductId] = StockLineStatus.Pending;
        }

        return SetStatus(OrderStatus.AwaitingValidation);
    }

    private async DurableTask<OrderSummary> RecordStock(OrderLine[] lines, bool[] confirmed)
    {
        ThrowIfCancelledByBuyer();
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

    private async DurableTask<OrderSummary> Cancel(int[] released, string? reason)
    {
        ThrowIfCancelledByBuyer();
        foreach (var productId in released)
        {
            _stock[productId] = StockLineStatus.Released;
        }

        return SetStatus(OrderStatus.Cancelled, reason);
    }

    private async DurableTask MarkReleased(int[] released)
    {
        foreach (var productId in released)
        {
            _stock[productId] = StockLineStatus.Released;
        }
    }

    // A transition which starts after the buyer cancelled during validation leaves the order unchanged; the workflow
    // observes the cancellation instead.
    private void ThrowIfCancelledByBuyer()
    {
        if (_order.Value!.Status is OrderStatus.Cancelled)
        {
            throw new OperationCanceledException("The buyer cancelled the order.");
        }
    }

    private OrderSummary SetStatus(OrderStatus status, string? description = null)
    {
        var order = _order.Value!;
        _order.Value = order with { Status = status, Version = order.Version + 1, Description = description };
        return _order.Value.ToSummary();
    }

    // Delivery order across tasks is unspecified; the buyer keeps the summary with the highest version.
    private ConfiguredDurableTask RecordAsync(string buyerId, OrderSummary summary) =>
        GrainFactory.GetGrain<IBuyerGrain>(buyerId).RecordOrderAsync(summary).WithId($"buyer-{summary.Version}");

    // A detached task under a root ID derived from the order and version, which the buyer retains, not the order.
    private ValueTask DetachRecordAsync(OrderSummary summary) =>
        GrainFactory.GetGrain<IBuyerGrain>(_order.Value!.BuyerId).RecordOrderAsync(summary)
            .DetachAsync($"order-{OrderId:N}-v{summary.Version}");

    private ICatalogItemGrain Catalog(int productId) => GrainFactory.GetGrain<ICatalogItemGrain>(productId);

    private static OrderResult Result(OrderSummary summary) => new(summary.Status, summary.Description);
}
