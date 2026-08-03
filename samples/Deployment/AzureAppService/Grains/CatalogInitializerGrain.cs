// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Grains;

internal sealed class CatalogInitializerGrain(
    [PersistentState(
        stateName: "CatalogInitialization",
        storageName: "shopping-cart")]
        IPersistentState<CatalogInitializationState> state)
    : Grain, ICatalogInitializerGrain
{
    public async Task EnsureSeededAsync(List<ProductDetails> products)
    {
        if (state.State.IsInitialized)
        {
            return;
        }

        var existingProductIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in Enum.GetNames<ProductCategory>())
        {
            var inventoryGrain = GrainFactory.GetGrain<IInventoryGrain>(category);
            await foreach (var product in inventoryGrain.GetAllProductsAsync())
            {
                existingProductIds.Add(product.Id);
            }
        }

        foreach (var product in products)
        {
            if (existingProductIds.Contains(product.Id))
            {
                continue;
            }

            var productGrain = GrainFactory.GetGrain<IProductGrain>(product.Id);
            await productGrain.CreateOrUpdateProductAsync(product);
        }

        state.State.IsInitialized = true;
        try
        {
            await state.WriteStateAsync();
        }
        catch
        {
            state.State.IsInitialized = false;
            throw;
        }
    }
}

[GenerateSerializer]
internal sealed class CatalogInitializationState
{
    [Id(0)]
    public bool IsInitialized { get; set; }
}
