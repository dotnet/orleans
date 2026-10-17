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
    private readonly IDurableValue<OrderState> _order;
    private readonly IDurableDictionary<int, StockLineStatus> _stock;

    public OrderGrain(
        IInbox inbox,
        IOutbox outbox,
        IOptions<OrderingOptions> options,
        OrderMessages messages)
    {
        _outbox = outbox;
        _options = options.Value;
        _messages = messages;
        _order = StateManager.GetOrAddValue<OrderState>("order");
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
        ReleaseConfirmedStock();
        SetStatus(OrderStatus.Cancelled, "Cancelled by the buyer.");
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

        var orderDate = DateTimeOffset.UtcNow;
        _order.Value = new OrderState(
            OrderId,
            request.BuyerId,
            Version: 0,
            OrderStatus.Submitted,
            orderDate,
            request.Lines,
            request.Address,
            request.CardLast4,
            Description: null);
        PublishStatus();

        // The inbox accepts this self-addressed message in the order's submission write
        // and processes it once the grace period ends.
        _outbox.Send(
            _messages.GracePeriodElapsed,
            new MessageId(OrderKey.CreateChildKey("grace-period")),
            this,
            new GracePeriodElapsed(),
            orderDate + _options.GracePeriod);
        context.Complete();
    }

    // The grace period gives the buyer time to cancel before stock is reserved.
    private void HandleGracePeriodElapsed(GracePeriodElapsed message, IInboxHandlerContext context)
    {
        var order = _order.Value!;
        if (order.Status is OrderStatus.Submitted)
        {
            foreach (var line in order.Lines)
            {
                _stock[line.ProductId] = StockLineStatus.Pending;
                _outbox.Send(
                    _messages.ReserveStock,
                    new MessageId(StockKey(line.ProductId)),
                    GrainFactory.GetGrain<ICatalogItemGrain>(line.ProductId),
                    new ReserveStock(OrderId, line.Units, this.GetGrainId()));
            }

            SetStatus(OrderStatus.AwaitingValidation);
        }

        context.Complete();
    }

    private void HandleStockResult(StockResult result, IInboxHandlerContext context)
    {
        var order = _order.Value!;
        if (order.Status is OrderStatus.AwaitingValidation)
        {
            _stock[result.ProductId] = result.Confirmed ? StockLineStatus.Confirmed : StockLineStatus.Rejected;
            if (!result.Confirmed)
            {
                ReleaseConfirmedStock();
                var productName = order.Lines.First(line => line.ProductId == result.ProductId).ProductName;
                SetStatus(OrderStatus.Cancelled, $"'{productName}' is out of stock.");
            }
            else if (_stock.Values.All(status => status is StockLineStatus.Confirmed))
            {
                SetStatus(OrderStatus.StockConfirmed);
                _outbox.Send(
                    _messages.AuthorizePayment,
                    new MessageId(OrderKey.CreateChildKey("payment")),
                    GrainFactory.GetGrain<IPaymentGrain>(OrderId),
                    new AuthorizePayment(order.Total, order.CardLast4, this.GetGrainId()));
            }
        }
        else if (order.Status is OrderStatus.Cancelled && result.Confirmed)
        {
            // This confirmation arrived after cancellation.
            Release(result.ProductId);
        }

        context.Complete();
    }

    private void HandlePaymentResult(PaymentResult result, IInboxHandlerContext context)
    {
        // Buyers cannot cancel once stock is confirmed, so the order awaits this result.
        Debug.Assert(_order.Value!.Status is OrderStatus.StockConfirmed);
        if (result.Succeeded)
        {
            foreach (var productId in _stock.Keys.ToArray())
            {
                _stock[productId] = StockLineStatus.Committed;
                _outbox.Send(
                    _messages.CommitStock,
                    new MessageId(StockKey(productId).CreateChildKey("commit")),
                    GrainFactory.GetGrain<ICatalogItemGrain>(productId),
                    new CommitStock(OrderId));
            }

            SetStatus(OrderStatus.Paid);
        }
        else
        {
            ReleaseConfirmedStock();
            SetStatus(OrderStatus.Cancelled, result.Reason);
        }

        context.Complete();
    }

    private void ReleaseConfirmedStock()
    {
        foreach (var (productId, status) in _stock.ToArray())
        {
            if (status is StockLineStatus.Confirmed)
            {
                Release(productId);
            }
        }
    }

    private void Release(int productId)
    {
        _stock[productId] = StockLineStatus.Released;
        _outbox.Send(
            _messages.ReleaseStock,
            new MessageId(StockKey(productId).CreateChildKey("release")),
            GrainFactory.GetGrain<ICatalogItemGrain>(productId),
            new ReleaseStock(OrderId));
    }

    private void SetStatus(OrderStatus status, string? description = null)
    {
        var order = _order.Value!;
        _order.Value = order with { Status = status, Version = order.Version + 1, Description = description };
        PublishStatus();
    }

    // Delivery order is unspecified; the buyer keeps the summary with the highest version.
    private void PublishStatus()
    {
        var order = _order.Value!;
        _outbox.Send(
            _messages.StatusChanged,
            new MessageId(OrderKey.CreateChildKey("status").CreateChildKey(order.Version.ToString())),
            GrainFactory.GetGrain<IBuyerGrain>(order.BuyerId),
            order.ToSummary());
    }

    private HierarchicalKey StockKey(int productId) =>
        OrderKey.CreateChildKey("stock").CreateChildKey(productId.ToString());
}
