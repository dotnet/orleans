// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Abstractions;

public interface IInventoryGrain : IGrainWithStringKey
{
    ValueTask<int> GetProductCount();

    IAsyncEnumerable<ProductDetails> GetAllProductsAsync();

    ValueTask AddOrUpdateProductAsync(ProductDetails productDetails);

    ValueTask RemoveProductAsync(string productId);
}
