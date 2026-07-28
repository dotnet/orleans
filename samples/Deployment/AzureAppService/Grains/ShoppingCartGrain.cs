// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Grains;

[Reentrant]
internal sealed class ShoppingCartGrain(
    [PersistentState(
            stateName: "ShoppingCart",
            storageName: "shopping-cart")]
        IPersistentState<Dictionary<string, CartItem>> state) : Grain, IShoppingCartGrain
{
    private readonly StateManager _stateManager = new(state);

    async Task<bool> IShoppingCartGrain.AddOrUpdateItemAsync(int quantity, ProductDetails product)
    {
        ArgumentNullException.ThrowIfNull(product.Id);
        var products = GrainFactory.GetGrain<IProductGrain>(product.Id);

        int? adjustedQuantity = null;
        if (state.State.TryGetValue(product.Id, out var existingItem))
        {
            adjustedQuantity = quantity - existingItem.Quantity;
        }

        var (isAvailable, claimedProduct) =
            await products.TryTakeProductAsync(adjustedQuantity ?? quantity);
        if (isAvailable && claimedProduct is not null)
        {
            var item = ToCartItem(quantity, claimedProduct);
            if (!string.IsNullOrEmpty(claimedProduct.Id))
            {
                state.State[claimedProduct.Id] = item;
            }

            await _stateManager.WriteStateAsync();
            return true;
        }

        return false;
    }

    async Task IShoppingCartGrain.EmptyCartAsync()
    {
        state.State.Clear();
        await _stateManager.ClearStateAsync();
    }

    Task<HashSet<CartItem>> IShoppingCartGrain.GetAllItemsAsync() =>
        Task.FromResult(state.State.Values.ToHashSet());

    Task<int> IShoppingCartGrain.GetTotalItemsInCartAsync() =>
        Task.FromResult(state.State.Count);

    async Task IShoppingCartGrain.RemoveItemAsync(ProductDetails product)
    {
        ArgumentNullException.ThrowIfNull(product.Id);
        var products = GrainFactory.GetGrain<IProductGrain>(product.Id);
        await products.ReturnProductAsync(product.Quantity);

        if (state.State.Remove(product.Id))
        {
            await _stateManager.WriteStateAsync();
        }
    }

    private CartItem ToCartItem(int quantity, ProductDetails product) =>
        new(this.GetPrimaryKeyString(), quantity, product);
}
