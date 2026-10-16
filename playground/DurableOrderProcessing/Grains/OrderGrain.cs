using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IOrderGrain : IGrainWithGuidKey
{
    Task<OrderDetails?> GetAsync();

    // Streams the order's committed history from its submission, and ends once the order ships or is cancelled.
    IAsyncEnumerable<OrderDetails> WatchAsync(CancellationToken cancellationToken);

    // The order workflow, which POST /api/orders starts as the operation orders/{orderId}.
    DurableTask<OrderResult> PlaceOrderAsync(PlaceOrder request);
}

// The order process manager, following eShop's ordering flow:
// Submitted -> (grace period) -> AwaitingValidation -> StockConfirmed -> Paid -> Shipped,
// or Cancelled when the basket is empty, stock is rejected, payment is declined or times out, or the buyer cancels
// before the order is paid.
// PlaceOrder is one durable task. After a deactivation, the runtime replays it from the start: completed child tasks
// return their recorded results, so the workflow resumes where it stopped. The order's state is its history: the
// workflow keeps the current snapshot in a local variable, computes each transition from the request, recorded results,
// and logical time, and appends the new snapshot to the history log in a recorded effect, DurableTask.RunInline. The effect
// appends once per position in the workflow, so a replay returns the recorded entry's sequence without appending it
// again. The buyer cancels the order by cancelling its operation until it is paid, and ships it by sending the
// operation's shipped signal.
public sealed class OrderGrain : DurableGrain, IOrderGrain
{
    public const string PaymentsClient = "payments";

    // A paid order's workflow waits for this signal, which POST /api/orders/operations/{orderId}/signals/shipped sends.
    public static readonly DurableSignal<Shipment> Shipped = new("shipped");

    private readonly OrderingOptions _options;
    private readonly IDurableHttpClient _payments;
    private readonly IDurableLog<OrderDetails> _history;

    public OrderGrain(IOptions<OrderingOptions> options, [FromKeyedServices(PaymentsClient)] IDurableHttpClient payments)
    {
        _options = options.Value;
        _payments = payments;
        _history = StateManager.GetOrAddLog<OrderDetails>("history");
    }

    private Guid OrderId => this.GetPrimaryKey();

    private OrderDetails? Latest => _history.Count == 0 ? null : _history[_history.NextSequence - 1];

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

    public async DurableTask<OrderResult> PlaceOrderAsync(PlaceOrder request)
    {
        // Takes the basket's items. The basket runs the call once for this task, so a retry or a replay reads the same
        // lines, and an order which a retried checkout reaches again takes no further items.
        var lines = await GrainFactory.GetGrain<IBasketGrain>(request.BuyerId).CheckoutAsync(OrderId).WithId("checkout");
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("The basket is empty.");
        }

        var order = OrderDetails.Submit(OrderId, new SubmitOrder(request.BuyerId, lines, request.Address, request.CardLast4), DurableTask.Current.UtcNow);
        DurableTask.RunInline(() => _history.Append(order));
        Record(order.Order);

        // Until the order is paid, cancelling its operation cancels this task. The next durable operation observes the
        // cancellation and throws OperationCanceledException, and the workflow voids the payment authorization and
        // releases the lines which validation may have reserved. A payment which times out or fails cancels the order
        // the same way.
        OrderLine[] reservable = [];
        DurableHttpOperation? authorization = null;
        PaymentResult? payment = null;
        string? cancellation = null;
        try
        {
            await DurableTask.Delay(_options.GracePeriod).WithId("grace-period");
            order = order.BeginValidation();
            reservable = lines;
            DurableTask.RunInline(() => _history.Append(order));
            Record(order.Order);

            // Reserves every line concurrently, as the children reserve/0, reserve/1, and so on. WhenAll waits for every
            // reservation, including after a cancellation, so each line's reservation is settled when it returns or throws.
            var confirmed = await lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units)).WhenAll().WithId("reserve");
            order = order.RecordStock(confirmed);
            if (Array.IndexOf(confirmed, false) is var rejected and >= 0)
            {
                return await CancelOrderAsync(order, lines, $"'{lines[rejected].ProductName}' is out of stock.");
            }

            DurableTask.RunInline(() => _history.Append(order));
            Record(order.Order);

            // Reservations which completed return normally after a cancellation, so the workflow checks for one before it
            // authorizes the payment. The recorded decision keeps a replay on the path which the first run took.
            if (DurableTask.RunInline(() => DurableTask.Current.IsTaskCancellationRequested))
            {
                throw new OperationCanceledException();
            }

            // Starts the authorization at the payment gateway. The request's Idempotency-Key derives from this step's
            // task ID, so a retry of the request, or of the step after a failure, reaches the same authorization. The
            // step is shielded, so a cancellation lets it finish and the workflow learns the authorization to void.
            authorization = await _payments.StartOperationAsync(() => new HttpRequestMessage(HttpMethod.Post, "authorizations")
            {
                Content = JsonContent.Create(new PaymentAuthorizationRequest(OrderId, order.Order.Total, request.CardLast4)),
            }).WithId("authorize-payment").Shielded();

            // Polls the authorization, as await-payment/0, until it completes or its timeout, await-payment/1, elapses.
            payment = await _payments.WaitForOperationAsync<PaymentResult>(authorization, new() { PollInterval = _options.PaymentPollInterval })
                .WaitAsync(_options.PaymentTimeout)
                .WithId("await-payment");
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            cancellation = DurableTask.Current.TaskCancellationReason;
        }
        catch (TimeoutException)
        {
            cancellation = "Payment authorization timed out.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any other failure of the payment steps, such as an unreachable gateway, a resilience handler's timeout or
            // open circuit, or a response which doesn't decode, fails the payment. A canceled attempt propagates, so the
            // next attempt replays the workflow.
            cancellation = "Payment authorization failed.";
        }

        if (payment is null)
        {
            await VoidPaymentAsync(authorization, cancellation);
            return await CancelOrderAsync(order, reservable, cancellation);
        }

        if (!payment.Succeeded)
        {
            return await CancelOrderAsync(order, lines, payment.Reason);
        }

        order = order.MarkPaid();
        DurableTask.RunInline(() => _history.Append(order));
        Record(order.Order);

        // A paid order ships: the commit and the wait for its shipment are shielded, so a cancellation doesn't reach them.
        // The wait returns at once for a shipment which arrived earlier.
        await lines.Select(line => Catalog(line.ProductId).CommitAsync(OrderId)).WhenAll().WithId("commit").Shielded();
        await Shipped.WaitAsync().Shielded();
        order = order.MarkShipped();
        DurableTask.RunInline(() => _history.Append(order));
        Record(order.Order);
        return Result(order);
    }

    // Cancels a started authorization at the gateway, which voids it, including one which completed after the order
    // stopped waiting. The request is shielded, so it runs although the order is cancelled. A void which fails leaves the
    // authorization to expire at the gateway, and the order is cancelled regardless.
    private async Task VoidPaymentAsync(DurableHttpOperation? authorization, string? reason)
    {
        if (authorization?.Location is not { } location)
        {
            return;
        }

        try
        {
            await _payments.CancelOperationAsync(location, reason).WithId("void-payment").Shielded();
        }
        catch (HttpRequestException)
        {
        }
    }

    // Releases the lines' reservations concurrently, then records the cancellation. Releasing is idempotent and returns
    // whether the line held a reservation, so the workflow releases each line without tracking which reservations ran.
    // The release is shielded, so the cancellation which ended the workflow lets this compensation run to completion.
    private async Task<OrderResult> CancelOrderAsync(OrderDetails order, OrderLine[] lines, string? reason)
    {
        var released = await lines.Select(line => Catalog(line.ProductId).ReleaseAsync(OrderId)).WhenAll()
            .WithId("release")
            .Shielded();
        order = order.Cancel([.. lines.Where((_, i) => released[i]).Select(line => line.ProductId)], reason);
        DurableTask.RunInline(() => _history.Append(order));
        Record(order.Order);
        return Result(order);
    }

    // Posts the buyer's projection update as a detached task, whose root ID derives from the workflow's ID, so the
    // workflow continues without waiting for the buyer and the update runs independently of the workflow. The update
    // commits with the workflow's next journal write, after the snapshot which the workflow records in the order's
    // history. Updates arrive in any order; the buyer keeps the summary with the highest version.
    private void Record(OrderState order) =>
        _ = GrainFactory.GetGrain<IBuyerGrain>(order.BuyerId).RecordOrderAsync(order.ToSummary()).Detached().Post();

    private ICatalogItemGrain Catalog(int productId) => GrainFactory.GetGrain<ICatalogItemGrain>(productId);

    private static OrderResult Result(OrderDetails order) => new(order.Order.Status, order.Order.Description);
}
