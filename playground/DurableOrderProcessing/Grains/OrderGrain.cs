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

    DurableTask<bool> CancelAsync();

    DurableTask<bool> ShipAsync();
}

// The order process manager, following eShop's ordering flow:
// Submitted -> (grace period) -> AwaitingValidation -> StockConfirmed -> Paid -> Shipped,
// or Cancelled when stock is rejected, payment is declined, or the buyer cancels during the grace period.
// PlaceOrder is one durable task. After a deactivation, the runtime replays it from the start: completed child tasks
// return their recorded results, so the workflow resumes where it stopped. Each state transition is a durable task
// method which the workflow awaits as an identified child, so its state change commits once with its recorded result,
// which a replay returns without running the transition again.
public sealed class OrderGrain : DurableGrain, IOrderGrain
{
    private readonly OrderingOptions _options;
    private readonly IDurableValue<OrderState> _order;
    private readonly IDurableDictionary<int, StockLineStatus> _stock;

    public OrderGrain(IOptions<OrderingOptions> options)
    {
        _options = options.Value;
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

        // Reserve each line's stock concurrently.
        var lines = request.Lines;
        var reservations = new ScheduledTask<bool>[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            reservations[i] = await Catalog(lines[i].ProductId)
                .ReserveAsync(OrderId, lines[i].Units)
                .WithId($"reserve-{lines[i].ProductId}")
                .ScheduleAsync();
        }

        await ScheduledTask.WhenAll(reservations);
        var confirmed = new bool[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            confirmed[i] = await reservations[i];
        }

        var reserved = lines.Where((_, i) => confirmed[i]).Select(line => line.ProductId).ToArray();
        if (Array.IndexOf(confirmed, false) is var rejected and >= 0)
        {
            await RecordStock(lines, confirmed).WithId("reject-stock");
            return await CompensateAsync(request.BuyerId, reserved, $"'{lines[rejected].ProductName}' is out of stock.");
        }

        summary = await RecordStock(lines, confirmed).WithId("confirm-stock");
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
        var commits = new ScheduledTask[reserved.Length];
        for (var i = 0; i < reserved.Length; i++)
        {
            commits[i] = await Catalog(reserved[i]).CommitAsync(OrderId).WithId($"commit-{reserved[i]}").ScheduleAsync();
        }

        await ScheduledTask.WhenAll(commits);
        return Result(summary);
    }

    // The endpoint schedules each cancel request as its own task, which records its decision once.
    public async DurableTask<bool> CancelAsync()
    {
        if (await CancelByBuyer().WithId("cancel") is not { } summary)
        {
            return false;
        }

        // Durable cancellation ends the workflow's grace period.
        await this.GetDurableTask<OrderResult>(PlaceOrderTaskId(OrderId)).CancelAsync();
        await RecordAsync(_order.Value!.BuyerId, summary);
        return true;
    }

    public async DurableTask<bool> ShipAsync()
    {
        if (await Ship().WithId("ship") is not { } summary)
        {
            return false;
        }

        await RecordAsync(_order.Value!.BuyerId, summary);
        return true;
    }

    // Releases reserved stock in reverse order of reservation, then cancels the order.
    private async Task<OrderResult> CompensateAsync(string buyerId, int[] reserved, string? reason)
    {
        for (var i = reserved.Length - 1; i >= 0; i--)
        {
            await Catalog(reserved[i]).ReleaseAsync(OrderId).WithId($"release-{reserved[i]}");
        }

        var summary = await Cancel(reserved, reason).WithId("cancel");
        await RecordAsync(buyerId, summary);
        return Result(summary);
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
        foreach (var productId in released)
        {
            _stock[productId] = StockLineStatus.Released;
        }

        return SetStatus(OrderStatus.Cancelled, reason);
    }

    private async DurableTask<OrderSummary?> CancelByBuyer() =>
        _order.Value?.Status is OrderStatus.Submitted ? SetStatus(OrderStatus.Cancelled, "Cancelled by the buyer.") : null;

    private async DurableTask<OrderSummary?> Ship() =>
        _order.Value?.Status is OrderStatus.Paid ? SetStatus(OrderStatus.Shipped) : null;

    private OrderSummary SetStatus(OrderStatus status, string? description = null)
    {
        var order = _order.Value!;
        _order.Value = order with { Status = status, Version = order.Version + 1, Description = description };
        return _order.Value.ToSummary();
    }

    // Delivery order across tasks is unspecified; the buyer keeps the summary with the highest version.
    private ConfiguredDurableTask RecordAsync(string buyerId, OrderSummary summary) =>
        GrainFactory.GetGrain<IBuyerGrain>(buyerId).RecordOrderAsync(summary).WithId($"buyer-{summary.Version}");

    private ICatalogItemGrain Catalog(int productId) => GrainFactory.GetGrain<ICatalogItemGrain>(productId);

    private static OrderResult Result(OrderSummary summary) => new(summary.Status, summary.Description);
}
