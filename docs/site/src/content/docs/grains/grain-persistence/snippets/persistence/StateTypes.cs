using Orleans;
using Orleans.Runtime;

namespace Orleans.Docs.Snippets.Persistence;

// <profile_state>
[GenerateSerializer]
public class ProfileState
{
    [Id(0)]
    public string Name { get; set; }

    [Id(1)]
    public Date DateOfBirth { get; set; }
}
// </profile_state>

// <cart_state>
[GenerateSerializer]
public class CartState
{
    [Id(0)]
    public List<CartItem> Items { get; set; } = new();
}

[GenerateSerializer]
public class CartItem
{
    [Id(0)]
    public string ProductId { get; set; }

    [Id(1)]
    public int Quantity { get; set; }
}
// </cart_state>

// Placeholder for Date type used in docs
[GenerateSerializer]
public struct Date
{
    [Id(0)]
    public int Year { get; set; }

    [Id(1)]
    public int Month { get; set; }

    [Id(2)]
    public int Day { get; set; }
}
