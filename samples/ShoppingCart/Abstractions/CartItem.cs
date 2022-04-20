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