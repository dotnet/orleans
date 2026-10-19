using System.Diagnostics;
using Microsoft.Extensions.Options;
using Orleans.Journaling;
using Orleans.Messaging;

namespace OrderProcessing.Grains;

public interface IOrderGrain : IGrainWithGuidKey
{
    Task<OrderDetails?> GetAsync();

    Task<bool> CancelAsync();

    Task<bool> ShipAsync();
}

// The order process manager, following eShop's ordering flow:
// Submitted -> (grace period) -> AwaitingValidation -> StockConfirmed -> Paid -> Shipped,
// or Cancelled when stock is rejected, payment is declined, or the buyer cancels before stock is confirmed.
// Every transition changes journaled state and stages outgoing messages in one atomic journal write.
public sealed class OrderGrain : DurableGrain, IOrderGrain
{
    private readonly IOutbox _outbox;
    private readonly OrderingOptions _options;
    private readonly OrderMessages _messages;
    private readonly TimeProvider _timeProvider;
    private readonly IDurableValue<OrderState> _order;
    private readonly IDurableValue<DateTimeOffset?> _submissionDate;
    private readonly IDurableDictionary<int, StockLineStatus> _stock;

    public OrderGrain(
        IInbox inbox,
        IOutbox outbox,
        IOptions<OrderingOptions> options,
        OrderMessages messages,
        TimeProvider timeProvider)
    {
        _outbox = outbox;
        _options = options.Value;
        _messages = messages;
        _timeProvider = timeProvider;
        _order = StateManager.GetOrAddValue<OrderState>("order");
        _submissionDate = StateManager.GetOrAddValue<DateTimeOffset?>("submission-date");
        _stock = StateManager.GetOrAddDictionary<int, StockLineStatus>("stock");
        inbox.Register(messages.SubmitOrder, HandleSubmit)
            .Register(messages.GracePeriodElapsed, HandleGracePeriodElapsed)
            .Register(messages.StockResult, HandleStockResult)
            .Register(messages.PaymentResult, HandlePaymentResult);
    }

    private Guid OrderId => this.GetPrimaryKey();

    private HierarchicalKey OrderKey => HierarchicalKey.Create("orders", OrderId.ToString("N"));

    public Task<OrderDetails?> GetAsync()
    {
        var order = _order.Value;
        var details = order is null
            ? null
            : new OrderDetails(order, _stock.Select(line => new StockLine(line.Key, line.Value)).ToArray());
        return Task.FromResult(details);
    }

    public async Task<bool> CancelAsync()
    {
        if (_order.Value?.Status is not (OrderStatus.Submitted or OrderStatus.AwaitingValidation))
        {
            return false;
        }

        // Pending reservations are released when their confirmations arrive.
        Cancel("Cancelled by the buyer.");
        await WriteStateAsync();
        return true;
    }

    public async Task<bool> ShipAsync()
    {
        if (_order.Value?.Status is not OrderStatus.Paid)
        {
            return false;
        }

        SetStatus(OrderStatus.Shipped);
        await WriteStateAsync();
        return true;
    }

    private void HandleSubmit(SubmitOrder request, IInboxHandlerContext context)
    {
        if (_order.Value is not null)
        {
            context.Fail($"Order {OrderId} already exists.");
            return;
        }

        var orderDate = request.OrderDate;
        if (orderDate == default)
        {
            // Legacy submissions carry no date. Its private intent survives a failed send and journal recovery.
            if (_submissionDate.Value is not { } captured)
            {
                captured = _timeProvider.GetUtcNow();
                _submissionDate.Value = captured;
            }

            orderDate = captured;
        }

        var order = new OrderState(
            OrderId,
            request.BuyerId,
            Version: 0,
            OrderStatus.Submitted,
            orderDate,
            request.Lines,
            request.Address,
            request.CardLast4,
            Description: null);

        // The inbox accepts this self-addressed message in the order's submission write
        // and processes it once the grace period ends. The submission fixes its time across handler retries.
        var status = CreateStatus(order);
        var gracePeriod = _messages.GracePeriodElapsed.Create(
            new MessageId(OrderKey.CreateChildKey("grace-period")),
            this.GetGrainId(),
            new GracePeriodElapsed(),
            order.OrderDate + _options.GracePeriod);
        _outbox.Send(status);
        _outbox.Send(gracePeriod);
        _order.Value = order;
        context.Complete();
    }

    // The grace period gives the buyer time to cancel before stock is reserved.
    private void HandleGracePeriodElapsed(GracePeriodElapsed message, IInboxHandlerContext context)
    {
        var order = _order.Value!;
        if (order.Status is OrderStatus.Submitted)
        {
            var next = NextStatus(OrderStatus.AwaitingValidation);
            var messages = order.Lines.Select(line => _messages.ReserveStock.Create(
                new MessageId(StockKey(line.ProductId)),
                GrainFactory.GetGrain<ICatalogItemGrain>(line.ProductId).GetGrainId(),
                new ReserveStock(OrderId, line.Units, this.GetGrainId()))).Append(CreateStatus(next)).ToArray();
            foreach (var outgoing in messages)
            {
                _outbox.Send(outgoing);
            }

            foreach (var line in order.Lines) _stock[line.ProductId] = StockLineStatus.Pending;
            _order.Value = next;
        }

        context.Complete();
    }

    private void HandleStockResult(StockResult result, IInboxHandlerContext context)
    {
        var order = _order.Value!;
        if (order.Status is OrderStatus.AwaitingValidation)
        {
            if (!result.Confirmed)
            {
                var productName = order.Lines.First(line => line.ProductId == result.ProductId).ProductName;
                Cancel($"'{productName}' is out of stock.");
                _stock[result.ProductId] = StockLineStatus.Rejected;
            }
            else if (_stock.All(line => line.Key == result.ProductId || line.Value is StockLineStatus.Confirmed))
            {
                var next = NextStatus(OrderStatus.StockConfirmed);
                var payment = _messages.AuthorizePayment.Create(
                    new MessageId(OrderKey.CreateChildKey("payment")),
                    GrainFactory.GetGrain<IPaymentGrain>(OrderId).GetGrainId(),
                    new AuthorizePayment(order.Total, order.CardLast4, this.GetGrainId()));
                var status = CreateStatus(next);
                _outbox.Send(payment);
                _outbox.Send(status);
                _stock[result.ProductId] = StockLineStatus.Confirmed;
                _order.Value = next;
            }
            else
            {
                _stock[result.ProductId] = StockLineStatus.Confirmed;
            }
        }
        else if (order.Status is OrderStatus.Cancelled && result.Confirmed)
        {
            // This confirmation arrived after cancellation.
            _outbox.Send(CreateRelease(result.ProductId));
            _stock[result.ProductId] = StockLineStatus.Released;
        }

        context.Complete();
    }

    private void HandlePaymentResult(PaymentResult result, IInboxHandlerContext context)
    {
        // Buyers cannot cancel once stock is confirmed, so the order awaits this result.
        Debug.Assert(_order.Value!.Status is OrderStatus.StockConfirmed);
        if (result.Succeeded)
        {
            var next = NextStatus(OrderStatus.Paid);
            var products = _stock.Keys.ToArray();
            var messages = products.Select(productId => _messages.CommitStock.Create(
                new MessageId(StockKey(productId).CreateChildKey("commit")),
                GrainFactory.GetGrain<ICatalogItemGrain>(productId).GetGrainId(),
                new CommitStock(OrderId))).Append(CreateStatus(next)).ToArray();
            foreach (var outgoing in messages)
            {
                _outbox.Send(outgoing);
            }

            foreach (var productId in products) _stock[productId] = StockLineStatus.Committed;
            _order.Value = next;
        }
        else
        {
            Cancel(result.Reason);
        }

        context.Complete();
    }

    private void Cancel(string? description)
    {
        var next = NextStatus(OrderStatus.Cancelled, description);
        var products = _stock.Where(line => line.Value is StockLineStatus.Confirmed).Select(line => line.Key).ToArray();
        var messages = products.Select(CreateRelease).Append(CreateStatus(next)).ToArray();
        foreach (var outgoing in messages)
        {
            _outbox.Send(outgoing);
        }

        foreach (var productId in products) _stock[productId] = StockLineStatus.Released;
        _order.Value = next;
    }

    private OutboxMessage CreateRelease(int productId) =>
        _messages.ReleaseStock.Create(
            new MessageId(StockKey(productId).CreateChildKey("release")),
            GrainFactory.GetGrain<ICatalogItemGrain>(productId).GetGrainId(),
            new ReleaseStock(OrderId));

    private void SetStatus(OrderStatus status)
    {
        var next = NextStatus(status);
        _outbox.Send(CreateStatus(next));
        _order.Value = next;
    }

    private OrderState NextStatus(OrderStatus status, string? description = null)
    {
        var order = _order.Value!;
        return order with { Status = status, Version = order.Version + 1, Description = description };
    }

    // Delivery order is unspecified; the buyer keeps the summary with the highest version.
    private OutboxMessage CreateStatus(OrderState order) =>
        _messages.StatusChanged.Create(
            new MessageId(OrderKey.CreateChildKey("status").CreateChildKey(order.Version.ToString())),
            GrainFactory.GetGrain<IBuyerGrain>(order.BuyerId).GetGrainId(),
            order.ToSummary());

    private HierarchicalKey StockKey(int productId) =>
        OrderKey.CreateChildKey("stock").CreateChildKey(productId.ToString());
}
