// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Grains;

[Reentrant]
public sealed class ShoppingCartGrain(
    [PersistentState(
            stateName: "ShoppingCart",
            storageName: "shopping-cart")]
        IPersistentState<Dictionary<string, CartItem>> cart) : Grain, IShoppingCartGrain
{
    async Task<bool> IShoppingCartGrain.AddOrUpdateItemAsync(int quantity, ProductDetails product)
    {
        var products = GrainFactory.GetGrain<IProductGrain>(product.Id);

        int? adjustedQuantity = null;
        if (cart.State.TryGetValue(product.Id, out var existingItem))
        {
            adjustedQuantity = quantity - existingItem.Quantity;
        }

        var (isAvailable, claimedProduct) =
            await products.TryTakeProductAsync(adjustedQuantity ?? quantity);
        if (isAvailable && claimedProduct is not null)
        {
            var item = ToCartItem(quantity, claimedProduct);
            cart.State[claimedProduct.Id] = item;

            await cart.WriteStateAsync();
            return true;
        }

        return false;
    }

    Task IShoppingCartGrain.EmptyCartAsync()
    {
        cart.State.Clear();
        return cart.ClearStateAsync();
    }

    Task<HashSet<CartItem>> IShoppingCartGrain.GetAllItemsAsync() =>
        Task.FromResult(cart.State.Values.ToHashSet());

    Task<int> IShoppingCartGrain.GetTotalItemsInCartAsync() =>
        Task.FromResult(cart.State.Count);

    async Task IShoppingCartGrain.RemoveItemAsync(ProductDetails product)
    {
        var products = GrainFactory.GetGrain<IProductGrain>(product.Id);
        await products.ReturnProductAsync(product.Quantity);

        if (cart.State.Remove(product.Id))
        {
            await cart.WriteStateAsync();
        }
    }

    private CartItem ToCartItem(int quantity, ProductDetails product) =>
        new(this.GetPrimaryKeyString(), quantity, product);
}
