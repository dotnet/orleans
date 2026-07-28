// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Abstractions;

[GenerateSerializer, Immutable]
[Alias("Orleans.ShoppingCart.Abstractions.CartItem")]
public sealed record class CartItem(
    string UserId,
    int Quantity,
    ProductDetails Product)
{
    [JsonIgnore]
    public decimal TotalPrice =>
        Math.Round(Quantity * Product.UnitPrice, 2);
}