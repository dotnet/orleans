// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Abstractions;

public interface ICatalogInitializerGrain : IGrainWithStringKey
{
    Task EnsureSeededAsync(List<ProductDetails> products);
}
