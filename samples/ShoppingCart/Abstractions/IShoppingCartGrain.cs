// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Abstractions;

public interface IShoppingCartGrain : IGrainWithStringKey
{
    /// <summary>
    /// Adds the given <paramref name="quantity"/> of the corresponding
    /// <paramref name="product"/> to the shopping cart.
    /// </summary>
    Task<bool> AddOrUpdateItemAsync(int quantity, ProductDetails product);

    /// <summary>
    /// Removes the given <paramref name="product" /> from the shopping cart.
    /// </summary>
    Task RemoveItemAsync(ProductDetails product);
    
    /// <summary>
    /// Gets all the items in the shopping cart.
    /// </summary>
    Task<HashSet<CartItem>> GetAllItemsAsync();

    /// <summary>
    /// Gets the number of items in the shopping cart.
    /// </summary>
    Task<int> GetTotalItemsInCartAsync();

    /// <summary>
    /// Removes all items from the shopping cart.
    /// </summary>
    Task EmptyCartAsync();
}