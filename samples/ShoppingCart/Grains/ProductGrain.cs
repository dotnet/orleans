// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Grains;

internal class ProductGrain : Grain, IProductGrain
{
    private readonly IPersistentState<ProductDetails> _product;

    public ProductGrain(
        [PersistentState(
            stateName: "Product",
            storageName: "shopping-cart")]
        IPersistentState<ProductDetails> product) => _product = product;

    Task<int> IProductGrain.GetProductAvailabilityAsync() => 
        Task.FromResult(_product.State.Quantity);

    Task<ProductDetails> IProductGrain.GetProductDetailsAsync() => 
        Task.FromResult(_product.State);

    Task IProductGrain.ReturnProductAsync(int quantity) =>
        UpdateStateAsync(_product.State with
        {
            Quantity = _product.State.Quantity + quantity
        });

    async Task<(bool IsAvailable, ProductDetails? ProductDetails)> IProductGrain.TryTakeProductAsync(int quantity)
    {
        if (_product.State.Quantity < quantity)
        {
            return (false, null);
        }

        var updatedState = _product.State with
        {
            Quantity = _product.State.Quantity - quantity
        };

        await UpdateStateAsync(updatedState);

        return (true, _product.State);
    }

    Task IProductGrain.CreateOrUpdateProductAsync(ProductDetails productDetails) =>
        UpdateStateAsync(productDetails);

    private async Task UpdateStateAsync(ProductDetails product)
    {
        var oldCategory = _product.State.Category;

        _product.State = product;
        await _product.WriteStateAsync();

        var inventoryGrain = GrainFactory.GetGrain<IInventoryGrain>(_product.State.Category.ToString());
        await inventoryGrain.AddOrUpdateProductAsync(product);

        if (oldCategory != product.Category)
        {
            // If category changed, remove the product from the old inventory grain.
            var oldInventoryGrain = GrainFactory.GetGrain<IInventoryGrain>(oldCategory.ToString());
            await oldInventoryGrain.RemoveProductAsync(product.Id);
        }
    }
}
