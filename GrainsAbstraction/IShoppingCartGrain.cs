using eShop.Domain.ShoppingCart.Entity;
using Orleans;

namespace GrainsAbstraction;


[GenerateSerializer]
public record ItemResponse(decimal Price, int Quantity, string Description, Guid Id);

[GenerateSerializer]
public record ShoppingCartResponse(Guid Id, ItemResponse[] Items);

public static class ItemExtensions
{
    public static ItemResponse ToItemResponse(this CartItem item)
        => new (item.Price, item.Quantity, item.Description, item.StockItemId);
}

public static class ShoppingCartEntityExtensions
{
    public static ShoppingCartResponse ToCartResponse(this ShoppingCartEntity cart)
        => new (cart.Id, cart.Items.Select(i => new ItemResponse(i.Price, i.Quantity, i.Description, i.Id)).ToArray());
}

public interface IShoppingCartGrain : IGrainWithGuidKey
{
    public Task<ShoppingCartResponse> Get();
    public Task<ShoppingCartResponse> AddItem(GrainCancellationToken grainCancellationToken, Guid itemId);
}