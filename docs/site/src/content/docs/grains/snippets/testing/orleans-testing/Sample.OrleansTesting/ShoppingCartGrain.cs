using Orleans.Runtime;

namespace Tests;

// <testkit_grain>
public interface IShoppingCartGrain : IGrainWithStringKey
{
    Task AddItem(string item);
}

public interface IAuditGrain : IGrainWithStringKey
{
    Task RecordItemAdded(string item);
}

[GenerateSerializer]
public sealed class ShoppingCartState
{
    [Id(0)]
    public List<string> Items { get; set; } = [];
}

public sealed class ShoppingCartGrain(
    [PersistentState("cart")] IPersistentState<ShoppingCartState> state)
    : Grain, IShoppingCartGrain
{
    public async Task AddItem(string item)
    {
        state.State.Items.Add(item);
        await state.WriteStateAsync();

        var audit = GrainFactory.GetGrain<IAuditGrain>(this.GetPrimaryKeyString());
        await audit.RecordItemAdded(item);
    }
}
// </testkit_grain>
