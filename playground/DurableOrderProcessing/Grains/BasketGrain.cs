using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IBasketGrain : IGrainWithStringKey
{
    Task<BasketItem[]> GetAsync();

    Task<BasketItem[]> SetQuantityAsync(int productId, int quantity);

    Task<Guid?> CheckoutAsync(CheckoutRequest request);
}

// The buyer's basket. Checkout clears the basket and detaches the order's PlaceOrder workflow in one journal write:
// a durable grain stages the invocation in its outbox, which replaces eShop's integration event log.
public sealed class BasketGrain : DurableGrain, IBasketGrain
{
    private static readonly TimeSpan CheckoutRetention = TimeSpan.FromDays(7);

    private readonly TimeProvider _timeProvider;
    private readonly IDurableDictionary<int, BasketItem> _items;
    private readonly IDurableDictionary<Guid, DateTimeOffset> _checkouts;

    public BasketGrain(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _items = StateManager.GetOrAddDictionary<int, BasketItem>("items");
        _checkouts = StateManager.GetOrAddDictionary<Guid, DateTimeOffset>("checkouts");
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

    // The request ID becomes the order ID. The basket remembers its checkouts for CheckoutRetention, so a retried
    // checkout within that period returns the same order, even after later checkouts.
    public async Task<Guid?> CheckoutAsync(CheckoutRequest request)
    {
        if (_checkouts.ContainsKey(request.RequestId))
        {
            // A checkout whose write failed before committing keeps its changes staged, so the retry writes them.
            await WriteStateAsync();
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
        var now = _timeProvider.GetUtcNow();
        foreach (var id in _checkouts.Where(pair => now - pair.Value >= CheckoutRetention).Select(pair => pair.Key).ToArray())
        {
            _checkouts.Remove(id);
        }

        _checkouts[orderId] = now;

        // The workflow is posted to the outbox with the cleared basket, and both commit in one journal write. The order ID
        // derives the workflow's root task ID, so the order runs one workflow, which it retains with its result; the
        // basket keeps no state for it.
        _ = GrainFactory.GetGrain<IOrderGrain>(orderId)
            .PlaceOrderAsync(new SubmitOrder(this.GetPrimaryKeyString(), lines, request.Address, request.CardNumber[^4..]))
            .Detached()
            .WithId(OrderGrain.PlaceOrderTaskId(orderId))
            .Post();
        await WriteStateAsync();
        return orderId;
    }
}
