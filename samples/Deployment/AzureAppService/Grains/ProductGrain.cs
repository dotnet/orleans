// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Grains;

[Reentrant]
internal sealed class ProductGrain(
    [PersistentState(
            stateName: "Product",
            storageName: "shopping-cart")]
        IPersistentState<ProductDetails> state) : Grain, IProductGrain
{
    private readonly StateManager _stateManager = new(state);

    Task<int> IProductGrain.GetProductAvailabilityAsync() =>
        Task.FromResult(state.State.Quantity);

    Task<ProductDetails> IProductGrain.GetProductDetailsAsync() =>
        Task.FromResult(state.State);

    Task IProductGrain.ReturnProductAsync(int quantity) =>
        UpdateStateAsync(state.State with
        {
            Quantity = state.State.Quantity + quantity
        });

    async Task<(bool IsAvailable, ProductDetails? ProductDetails)> IProductGrain.TryTakeProductAsync(int quantity)
    {
        if (state.State.Quantity < quantity)
        {
            return (false, null);
        }

        var updatedState = state.State with
        {
            Quantity = state.State.Quantity - quantity
        };

        await UpdateStateAsync(updatedState);

        return (true, state.State);
    }

    Task IProductGrain.CreateOrUpdateProductAsync(ProductDetails productDetails) =>
        UpdateStateAsync(productDetails);

    private async Task UpdateStateAsync(ProductDetails product)
    {
        ArgumentNullException.ThrowIfNull(product.Id);
        var oldCategory = state.State.Category;

        state.State = product;
        await _stateManager.WriteStateAsync();

        var inventoryGrain = GrainFactory.GetGrain<IInventoryGrain>(state.State.Category.ToString());
        await inventoryGrain.AddOrUpdateProductAsync(product);

        if (oldCategory != product.Category)
        {
            // If category changed, remove the product from the old inventory grain.
            var oldInventoryGrain = GrainFactory.GetGrain<IInventoryGrain>(oldCategory.ToString());
            await oldInventoryGrain.RemoveProductAsync(product.Id);
        }
    }
}
