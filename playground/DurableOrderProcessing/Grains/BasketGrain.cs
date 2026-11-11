using Orleans.DurableTasks;
using Orleans.Journaling;

namespace DurableOrderProcessing.Grains;

public interface IBasketGrain : IGrainWithStringKey
{
    Task<BasketItem[]> GetAsync();

    Task<BasketItem[]> SetQuantityAsync(int productId, int quantity);

    // Hands the basket's items to the order orderId and clears the basket.
    DurableTask<OrderLine[]> CheckoutAsync(Guid orderId);
}

// The buyer's basket. Quantities are set, not incremented, so a repeated or retried update is idempotent.
public sealed class BasketGrain : DurableGrain, IBasketGrain
{
    private readonly IDurableDictionary<int, BasketItem> _items;

    public BasketGrain()
    {
        _items = StateManager.GetOrAddDictionary<int, BasketItem>("items");
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

    // The order's workflow calls this as its first step. The cleared basket commits with the task's result, and the
    // runtime runs the call once for the order's task, so a retried or replayed checkout returns the same lines.
    public async DurableTask<OrderLine[]> CheckoutAsync(Guid orderId)
    {
        OrderLine[] lines = [.. _items.Values.Select(item => new OrderLine(item.ProductId, item.ProductName, item.UnitPrice, item.Quantity))];
        _items.Clear();
        return lines;
    }
}
