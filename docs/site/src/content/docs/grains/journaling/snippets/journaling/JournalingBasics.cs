using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Journaling;

namespace Orleans.Docs.Snippets.Journaling;

public interface IShoppingCartGrain : IGrainWithStringKey
{
    ValueTask AddItem(string itemId, int quantity, CancellationToken cancellationToken);

    ValueTask<IReadOnlyDictionary<string, int>> GetItems(CancellationToken cancellationToken);
}

// <durable_shopping_cart>
public sealed class ShoppingCartGrain(IDurableStateManager stateManager)
    : Grain, IShoppingCartGrain
{
    // Declare the cart component during construction, before Orleans recovers the grain's state.
    private readonly IDurableDictionary<string, int> _items =
        stateManager.GetOrAddDictionary<string, int>("cart-items");

    public async ValueTask AddItem(string itemId, int quantity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items[itemId] = quantity;
        await stateManager.WriteStateAsync(cancellationToken);
    }

    public ValueTask<IReadOnlyDictionary<string, int>> GetItems(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, int>>(
            new Dictionary<string, int>(_items));
    }
}
// </durable_shopping_cart>

public interface IKeyedCounterGrain : IGrainWithStringKey
{
    ValueTask<int> Increment(CancellationToken cancellationToken);
}

// <keyed_durable_counter>
public sealed class KeyedCounterGrain(
    [FromKeyedServices("count")] IDurableValue<int> count)
    : DurableGrain, IKeyedCounterGrain
{
    public async ValueTask<int> Increment(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        count.Value++;
        await WriteStateAsync(cancellationToken);
        return count.Value;
    }
}
// </keyed_durable_counter>

public static class StandaloneJournaling
{
    // <standalone_durable_state>
    public static async ValueTask<int> Increment(
        IJournaledStateManagerFactory factory,
        JournalId journalId,
        CancellationToken cancellationToken)
    {
        await using var manager = factory.CreateStandalone(journalId);
        var count = manager.GetOrAddValue<int>("count");
        await manager.InitializeAsync(cancellationToken);

        count.Value++;
        await manager.WriteStateAsync(cancellationToken);
        return count.Value;
    }
    // </standalone_durable_state>
}
