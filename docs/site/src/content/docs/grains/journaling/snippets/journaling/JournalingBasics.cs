using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace Orleans.Docs.Snippets.Journaling;

public interface IShoppingCartGrain : IGrainWithStringKey
{
    ValueTask AddItem(string itemId, int quantity);

    ValueTask<IReadOnlyDictionary<string, int>> GetItems();
}

// <durable_shopping_cart>
public sealed class ShoppingCartGrain(
    [FromKeyedServices("cart-items")]
    Orleans.Journaling.IDurableDictionary<string, int> items)
    : Orleans.Journaling.DurableGrain, IShoppingCartGrain
{
    public async ValueTask AddItem(string itemId, int quantity)
    {
        items[itemId] = quantity;
        await WriteStateAsync();
    }

    public ValueTask<IReadOnlyDictionary<string, int>> GetItems() =>
        ValueTask.FromResult<IReadOnlyDictionary<string, int>>(
            new Dictionary<string, int>(items));
}
// </durable_shopping_cart>
