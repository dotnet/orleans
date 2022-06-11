// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.Services;

public sealed class ProductService : BaseClusterService
{
    public ProductService(
        IHttpContextAccessor httpContextAccessor, IClusterClient client) :
        base(httpContextAccessor, client)
    {
    }

    public Task CreateOrUpdateProductAsync(ProductDetails product) =>
        _client.GetGrain<IProductGrain>(product.Id).CreateOrUpdateProductAsync(product);

    public Task<(bool IsAvailable, ProductDetails? ProductDetails)> TryTakeProductAsync(
        string productId, int quantity) =>
        TryUseGrain<IProductGrain, Task<(bool IsAvailable, ProductDetails? ProductDetails)>>(
            products => products.TryTakeProductAsync(quantity),
            productId,
            () => Task.FromResult<(bool IsAvailable, ProductDetails? ProductDetails)>(
                (false, null)));

    public Task ReturnProductAsync(string productId, int quantity) =>
        TryUseGrain<IProductGrain, Task>(
            products => products.ReturnProductAsync(quantity),
            productId,
            () => Task.CompletedTask);

    public Task<int> GetProductAvailability(string productId) =>
        TryUseGrain<IProductGrain, Task<int>>(
            products => products.GetProductAvailabilityAsync(),
            productId,
            () => Task.FromResult(0));
}
