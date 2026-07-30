// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.
using System.Linq;
namespace Orleans.ShoppingCart.Silo.Services;

public sealed class InventoryService(IClusterClient client)
{
    public async Task<HashSet<ProductDetails>> GetAllProductsAsync()
    {
        var allProducts = new HashSet<ProductDetails>();

        foreach (var category in Enum.GetNames<ProductCategory>())
        {
            await foreach (var product in client.GetGrain<IInventoryGrain>(category).GetAllProductsAsync())
            {
                allProducts.Add(product);
            }
        }

        return allProducts;
    }
}
