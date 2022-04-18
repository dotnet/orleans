namespace Orleans.ShoppingCart.Grains;

[Reentrant]
public sealed class ShoppingCartGrain : Grain, IShoppingCartGrain
{
    readonly IPersistentState<Dictionary<string, CartItem>> _cart;

    public ShoppingCartGrain(
        [PersistentState(
            stateName: "ShoppingCart",
            storageName: "shopping-cart")]
        IPersistentState<Dictionary<string, CartItem>> cart) => _cart = cart;

    async Task<bool> IShoppingCartGrain.AddOrUpdateItemAsync(int quantity, ProductDetails product)
    {
        var products = GrainFactory.GetGrain<IProductGrain>(product.Id);
   
        int? adjustedQuantity = null;
        if (_cart.State.TryGetValue(product.Id, out var existingItem))
        {
            adjustedQuantity = quantity - existingItem.Quantity;
        }

        var (isAvailable, claimedProduct) =
            await products.TryTakeProductAsync(adjustedQuantity ?? quantity);
        if (isAvailable && claimedProduct is not null)
        {
            var item = ToCartItem(quantity, claimedProduct);
            _cart.State[claimedProduct.Id] = item;

            await _cart.WriteStateAsync();
            return true;
        }

        return false;
    }

    Task IShoppingCartGrain.EmptyCartAsync()
    {
        _cart.State.Clear();
        return _cart.ClearStateAsync();
    }

    Task<HashSet<CartItem>> IShoppingCartGrain.GetAllItemsAsync() =>
        Task.FromResult(_cart.State.Values.ToHashSet());

    Task<int> IShoppingCartGrain.GetTotalItemsInCartAsync() =>
        Task.FromResult(_cart.State.Count);

    async Task IShoppingCartGrain.RemoveItemAsync(ProductDetails product)
    {
        var products = GrainFactory.GetGrain<IProductGrain>(product.Id);
        await products.ReturnProductAsync(product.Quantity);

        if (_cart.State.Remove(product.Id))
        {
            await _cart.WriteStateAsync();
        }
    }

    CartItem ToCartItem(int quantity, ProductDetails product) =>
        new(this.GetPrimaryKeyString(), quantity, product);
}
