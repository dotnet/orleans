// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.Services;

public sealed class ProductService(
    IClusterClient client,
    IAuthorizationService authorizationService)
{
    public async Task CreateOrUpdateProductAsync(
        ClaimsPrincipal user,
        ProductDetails product)
    {
        var authorizationResult = await authorizationService.AuthorizeAsync(
            user,
            AuthorizationPolicies.ProductManagement);
        if (!authorizationResult.Succeeded)
        {
            throw new UnauthorizedAccessException(
                "Product management authorization is required.");
        }

        await client.GetGrain<IProductGrain>(product.Id).CreateOrUpdateProductAsync(product);
    }
}
