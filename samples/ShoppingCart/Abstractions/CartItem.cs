// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Abstractions;

[Serializable, Immutable]
public sealed record class CartItem(
    string UserId,
    int Quantity,
    ProductDetails Product)
{
    [JsonIgnore]
    public decimal TotalPrice =>
        Math.Round(Quantity * Product.UnitPrice, 2);
}