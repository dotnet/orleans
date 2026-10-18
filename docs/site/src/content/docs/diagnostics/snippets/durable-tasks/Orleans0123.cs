using System.Collections.Immutable;

namespace Documentation.Diagnostics.DurableTasks;

[GenerateSerializer]
public sealed class Basket
{
    [Id(0)]
    public List<string> Items { get; init; } = [];
}

[GenerateSerializer, Immutable]
public sealed record Cart(ImmutableList<string> Items);

public sealed class CartGrain : DurableGrain
{
    private readonly IDurableValue<Basket> _basket;
    private readonly IDurableValue<Cart> _cart;

    public CartGrain()
    {
        _basket = StateManager.GetOrAddValue<Basket>("basket");
        _cart = StateManager.GetOrAddValue<Cart>("cart");
    }

#pragma warning disable ORLEANS0123
    // <orleans0123_violation>
    public async DurableTask AddAsync(string item)
    {
        await Reserve(item).WithId("reserve");

        // Changes the list which the value holds: no record holds the item, and a canceled attempt still adds it.
        _basket.Value!.Items.Add(item);
    }
    // </orleans0123_violation>
#pragma warning restore ORLEANS0123

    // <orleans0123_fix>
    public async DurableTask AddToCartAsync(string item)
    {
        await Reserve(item).WithId("reserve");

        // Assigns a new value, which the journal records and commits with the task's result.
        var cart = _cart.Value ?? new Cart([]);
        _cart.Value = cart with { Items = cart.Items.Add(item) };
    }
    // </orleans0123_fix>

    private static async DurableTask<bool> Reserve(string item) => true;
}
