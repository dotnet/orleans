using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IBasketGrain : IGrainWithStringKey
{
    Task<BasketItem[]> GetAsync();

    Task<BasketItem[]> SetQuantityAsync(int productId, int quantity);

    Task<Guid?> CheckoutAsync(CheckoutRequest request);
}

// The buyer's basket. Checkout clears the basket and schedules the order's PlaceOrder workflow in one journal write:
// a durable grain stages the invocation in its outbox, which replaces eShop's integration event log.
public sealed class BasketGrain : DurableGrain, IBasketGrain
{
    private readonly IDurableDictionary<int, BasketItem> _items;
    private readonly IDurableValue<Guid> _lastCheckout;

    public BasketGrain()
    {
        _items = StateManager.GetOrAddDictionary<int, BasketItem>("items");
        _lastCheckout = StateManager.GetOrAddValue<Guid>("last-checkout");
    }

    public Task<BasketItem[]> GetAsync() => Task.FromResult(_items.Values.ToArray());

    public async Task<BasketItem[]> SetQuantityAsync(int productId, int quantity)
    {
        if (quantity > 0)
        {
            var product = await GrainFactory.GetGrain<ICatalogItemGrain>(productId).GetAsync()
                ?? throw new ArgumentException($"Product {productId} does not exist.", nameof(productId));
            _items[productId] = new BasketItem(productId, product.Name, product.Price, quantity);
        }
        else
        {
            _items.Remove(productId);
        }

        await WriteStateAsync();
        return _items.Values.ToArray();
    }

    // The request ID becomes the order ID, so a retried checkout returns the same order.
    public async Task<Guid?> CheckoutAsync(CheckoutRequest request)
    {
        if (_lastCheckout.Value == request.RequestId)
        {
            return request.RequestId;
        }

        if (_items.Count == 0)
        {
            return null;
        }

        var orderId = request.RequestId;
        var lines = _items.Values
            .Select(item => new OrderLine(item.ProductId, item.ProductName, item.UnitPrice, item.Quantity))
            .ToArray();
        _items.Clear();
        _lastCheckout.Value = orderId;

        // Scheduling stages the invocation and writes the journal, committing the cleared basket with it.
        // The order ID derives the workflow's root task ID, so the order runs one workflow.
        await GrainFactory.GetGrain<IOrderGrain>(orderId)
            .PlaceOrderAsync(new SubmitOrder(this.GetPrimaryKeyString(), lines, request.Address, request.CardNumber[^4..]))
            .ScheduleAsync(OrderGrain.PlaceOrderTaskId(orderId));
        return orderId;
    }
}
