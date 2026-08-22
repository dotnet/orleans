using Orleans.Journaling;

namespace WorkflowsApp.Service;

public class ShoppingCartGrain(
    [FromKeyedServices("shopping-cart")] IDurableDictionary<string, int> cart,
    [FromKeyedServices("version")] IDurableValue<long> version) : DurableGrain, IShoppingCartGrain
{
    private readonly IDurableValue<long> _version = version;

    public async ValueTask<(bool Success, long Version)> UpdateItem(string itemId, int quantity, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (quantity == 0)
        {
            cart.Remove(itemId);
        }
        else
        {
            cart[itemId] = quantity;
        }

        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }

    public ValueTask<(Dictionary<string, int> Contents, long Version)> GetCart() => new((cart.ToDictionary(), _version.Value));
    public ValueTask<long> GetVersion() => new(_version.Value);

    public async ValueTask<(bool Success, long Version)> Clear(long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        cart.Clear();
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }
}
