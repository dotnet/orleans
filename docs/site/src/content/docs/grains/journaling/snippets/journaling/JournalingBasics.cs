using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace Orleans.Docs.Snippets.Journaling;

public interface IShoppingCartGrain : IGrainWithStringKey
{
    ValueTask AddItem(string itemId, int quantity, CancellationToken cancellationToken);

    ValueTask<IReadOnlyDictionary<string, int>> GetItems(CancellationToken cancellationToken);
}

// <durable_shopping_cart>
public sealed class ShoppingCartGrain(
    [FromKeyedServices("cart-items")]
    Orleans.Journaling.IDurableDictionary<string, int> items)
    : Orleans.Journaling.DurableGrain, IShoppingCartGrain
{
    public async ValueTask AddItem(string itemId, int quantity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        items[itemId] = quantity;
        await WriteStateAsync(cancellationToken);
    }

    public ValueTask<IReadOnlyDictionary<string, int>> GetItems(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, int>>(
            new Dictionary<string, int>(items));
    }
}
// </durable_shopping_cart>
