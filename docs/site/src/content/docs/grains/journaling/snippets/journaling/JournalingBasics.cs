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
        IDurableValueCommandCodec<int> codec,
        CancellationToken cancellationToken)
    {
        await using var stateManager = factory.CreateStandalone(journalId);
        var component = new CounterState(codec);
        stateManager.RegisterStateMachine("count", component);
        await stateManager.InitializeAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        component.Value++;
        await stateManager.WriteStateAsync(cancellationToken);
        return component.Value;
    }
    // </standalone_durable_state>

    private sealed class CounterState(IDurableValueCommandCodec<int> codec)
        : IStateMachine, IDurableValueCommandHandler<int>
    {
        public int Value { get; set; }

        public void Reset(JournalStreamWriter writer) => Value = 0;

        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);

        public void WritePendingEntries(JournalStreamWriter writer) => codec.WriteSet(Value, writer);

        public void WriteSnapshot(JournalStreamWriter writer) => codec.WriteSet(Value, writer);

        public void ApplySet(int value) => Value = value;
    }
}
