// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.Services;

public sealed class ShoppingCartService : BaseClusterService
{
    public ShoppingCartService(
        IHttpContextAccessor httpContextAccessor, IClusterClient client) :
        base(httpContextAccessor, client)
    {
    }

    public Task<HashSet<CartItem>> GetAllItemsAsync() =>
        TryUseGrain<IShoppingCartGrain, Task<HashSet<CartItem>>>(
            cart => cart.GetAllItemsAsync(),
            () => Task.FromResult(new HashSet<CartItem>()));

    public Task<int> GetCartCountAsync() =>
        TryUseGrain<IShoppingCartGrain, Task<int>>(
            cart => cart.GetTotalItemsInCartAsync(),
            () => Task.FromResult(0));

    public Task EmptyCartAsync() =>
        TryUseGrain<IShoppingCartGrain, Task>(
            cart => cart.EmptyCartAsync(), 
            () => Task.CompletedTask);

    public Task<bool> AddOrUpdateItemAsync(int quantity, ProductDetails product) =>
        TryUseGrain<IShoppingCartGrain, Task<bool>>(
            cart => cart.AddOrUpdateItemAsync(quantity, product),
            () => Task.FromResult(false));

    public Task RemoveItemAsync(ProductDetails product) =>
        TryUseGrain<IShoppingCartGrain, Task>(
            cart => cart.RemoveItemAsync(product),
            () => Task.CompletedTask);
}
