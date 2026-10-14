using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IOrderGrain : IGrainWithGuidKey
{
    Task<OrderDetails?> GetAsync();

    // Streams the order's committed history from its submission, and ends once the order ships or is cancelled.
    IAsyncEnumerable<OrderDetails> WatchAsync(CancellationToken cancellationToken);

    // The order workflow, which basket checkout detaches under PlaceOrderTaskId(orderId).
    DurableTask<OrderResult> PlaceOrderAsync(SubmitOrder request);

    Task<bool> CancelAsync();

    Task<bool> ShipAsync();
}

// The order process manager, following eShop's ordering flow:
// Submitted -> (grace period) -> AwaitingValidation -> StockConfirmed -> Paid -> Shipped,
// or Cancelled when stock is rejected, payment is declined, or the buyer cancels before stock is confirmed.
// PlaceOrder is one durable task. After a deactivation, the runtime replays it from the start: completed child tasks
// return their recorded results, so the workflow resumes where it stopped. The order's state is its history: the
// workflow keeps the current snapshot in a local variable, computes each transition from the request, recorded results,
// and logical time, and reports the new snapshot to the history log. A report appends once per position, so a replay
// returns the recorded entry's sequence without appending it again. Cancel and ship are ordinary methods which read the
// latest snapshot: cancel requests durable cancellation of the workflow with the buyer's reason, and ship sends the
// workflow its Shipped signal.
public sealed class OrderGrain : DurableGrain, IOrderGrain
{
    private const string CancelledByBuyer = "Cancelled by the buyer.";

    // A paid order's workflow waits for this signal, which ShipAsync sends.
    private static readonly DurableSignal<Shipment> Shipped = new("shipped");

    private readonly OrderingOptions _options;
    private readonly IDurableTaskManager _tasks;
    private readonly IDurableLog<OrderDetails> _history;

    public OrderGrain(IOptions<OrderingOptions> options, IDurableTaskManager tasks)
    {
        _options = options.Value;
        _tasks = tasks;
        _history = StateManager.GetOrAddLog<OrderDetails>("history");
    }

    private Guid OrderId => this.GetPrimaryKey();

    private OrderDetails? Latest => _history.Count == 0 ? null : _history[_history.NextSequence - 1];

    public static string PlaceOrderTaskId(Guid orderId) => $"place-order-{orderId:N}";

    public Task<OrderDetails?> GetAsync() => Task.FromResult(Latest);

    // Reads the history on the activation, so a watcher observes each snapshot once it commits. The enumeration's calls
    // interleave with the order's other calls, so a watcher doesn't delay the workflow.
    public async IAsyncEnumerable<OrderDetails> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var change in _history.ReadAsync(cancellationToken: cancellationToken))
        {
            if (change.Kind is LogEventKind.Sealed)
            {
                yield break;
            }

            if (change is { Kind: LogEventKind.Entry, Item: { } order })
            {
                yield return order;
                if (order.Order.Status is OrderStatus.Shipped or OrderStatus.Cancelled)
                {
                    yield break;
                }
            }
        }
    }

    public async DurableTask<OrderResult> PlaceOrderAsync(SubmitOrder request)
    {
        var lines = request.Lines;
        var order = OrderDetails.Submit(OrderId, request, DurableTask.Current.UtcNow);
        _history.Report(order);
        await RecordAsync(order.Order);

        // While the order awaits validation, CancelAsync can cancel this task. The next durable operation, up to the
        // payment authorization, observes the cancellation and throws OperationCanceledException, and the workflow
        // releases the lines which validation may have reserved.
        OrderLine[] reservable = [];
        PaymentResult payment;
        try
        {
            await DurableTask.Delay(_options.GracePeriod).WithId("grace-period");
            order = order.BeginValidation();
            reservable = lines;
            _history.Report(order);
            await RecordAsync(order.Order);

            // Reserves every line concurrently, as the children reserve/0, reserve/1, and so on. WhenAll waits for every
            // reservation, including after a cancellation, so each line's reservation is settled when it returns or throws.
            var confirmed = await DurableTask.WhenAll(
                lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units))).WithId("reserve");
            order = order.RecordStock(confirmed);
            if (Array.IndexOf(confirmed, false) is var rejected and >= 0)
            {
                return await CancelOrderAsync(order, lines, $"'{lines[rejected].ProductName}' is out of stock.");
            }

            _history.Report(order);
            await RecordAsync(order.Order);
            payment = await GrainFactory.GetGrain<IPaymentGrain>(OrderId)
                .AuthorizeAsync(order.Order.Total, request.CardLast4)
                .WithId("authorize-payment");
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsCancellationRequested)
        {
            return await CancelOrderAsync(order, reservable, DurableTask.Current.CancellationReason);
        }

        if (!payment.Succeeded)
        {
            return await CancelOrderAsync(order, lines, payment.Reason);
        }

        order = order.MarkPaid();
        _history.Report(order);
        await RecordAsync(order.Order);
        await DurableTask.WhenAll(lines.Select(line => Catalog(line.ProductId).CommitAsync(OrderId))).WithId("commit");

        // The order ships when ShipAsync signals it; the wait returns at once for a shipment which arrived earlier.
        await Shipped.WaitAsync();
        order = order.MarkShipped();
        _history.Report(order);
        await RecordAsync(order.Order);
        return Result(order);
    }

    // The workflow observes the cancellation during its grace period or stock validation, releases the stock it
    // reserved, and reports the cancellation with this reason.
    public async Task<bool> CancelAsync()
    {
        if (Latest?.Order.Status is not (OrderStatus.Submitted or OrderStatus.AwaitingValidation))
        {
            return false;
        }

        await _tasks.CancelAsync(PlaceOrderTaskId(OrderId), CancelledByBuyer);
        return true;
    }

    // The workflow records the first shipment, so shipping again conflicts with it, including before the workflow has
    // reported the Shipped status.
    public async Task<bool> ShipAsync()
    {
        if (Latest?.Order.Status is not OrderStatus.Paid)
        {
            return false;
        }

        var result = await _tasks.TrySignalAsync(PlaceOrderTaskId(OrderId), Shipped, new Shipment(DateTimeOffset.UtcNow));
        return result is DurableSignalResult.Accepted;
    }

    // Releases the lines' reservations concurrently, then reports the cancellation. Releasing is idempotent and returns
    // whether the line held a reservation, so the workflow releases each line without tracking which reservations ran.
    // The suppression scope keeps the workflow's cancellation from reaching this compensation, so it runs to completion.
    private async Task<OrderResult> CancelOrderAsync(OrderDetails order, OrderLine[] lines, string? reason)
    {
        using (DurableTask.Current.SuppressCancellation())
        {
            var released = await DurableTask.WhenAll(lines.Select(line => Catalog(line.ProductId).ReleaseAsync(OrderId)))
                .WithId("release");
            order = order.Cancel([.. lines.Where((_, i) => released[i]).Select(line => line.ProductId)], reason);
            _history.Report(order);
            await RecordAsync(order.Order);
            return Result(order);
        }
    }

    // Detaches the buyer's projection update under a root ID derived from the workflow's ID and the version, such as
    // "place-order-{orderId}:buyer-v2", so the workflow doesn't wait for the buyer and its cancellation doesn't reach the
    // update. The workflow records each snapshot in the order's history first. Updates arrive in any order; the buyer
    // keeps the summary with the highest version.
    private ValueTask RecordAsync(OrderState order) =>
        GrainFactory.GetGrain<IBuyerGrain>(order.BuyerId).RecordOrderAsync(order.ToSummary())
            .DetachAsync(DetachedTaskId.For($"buyer-v{order.Version}"));

    private ICatalogItemGrain Catalog(int productId) => GrainFactory.GetGrain<ICatalogItemGrain>(productId);

    private static OrderResult Result(OrderDetails order) => new(order.Order.Status, order.Order.Description);
}
