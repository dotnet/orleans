// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.Services;

public sealed class InventoryService : BaseClusterService
{
    public InventoryService(
        IHttpContextAccessor httpContextAccessor, IClusterClient client) :
        base(httpContextAccessor, client)
    {
    }

    public async Task<HashSet<ProductDetails>> GetAllProductsAsync()
    {
        var getAllProductsTasks = Enum.GetValues<ProductCategory>()
            .Select(category =>
                _client.GetGrain<IInventoryGrain>(category.ToString()))
            .Select(grain => grain.GetAllProductsAsync())
            .ToList();

        var allProducts = await Task.WhenAll(getAllProductsTasks);

        return new HashSet<ProductDetails>(allProducts.SelectMany(products => products));
    }
}
