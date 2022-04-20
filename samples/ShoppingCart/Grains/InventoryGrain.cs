namespace Orleans.ShoppingCart.Grains;

[Reentrant]
public sealed class InventoryGrain : Grain, IInventoryGrain
{
    readonly IPersistentState<HashSet<string>> _productIds;
    readonly Dictionary<string, ProductDetails> _productCache = new();

    public InventoryGrain(
        [PersistentState(
            stateName: "Inventory",
            storageName: "shopping-cart")]
        IPersistentState<HashSet<string>> state) => _productIds = state;

    public override Task OnActivateAsync() => PopulateProductCacheAsync();

    Task<HashSet<ProductDetails>> IInventoryGrain.GetAllProductsAsync() =>
        Task.FromResult(_productCache.Values.ToHashSet());

    async Task IInventoryGrain.AddOrUpdateProductAsync(ProductDetails product)
    {
        _productIds.State.Add(product.Id);
        _productCache[product.Id] = product;

        await _productIds.WriteStateAsync();
    }

    public async Task RemoveProductAsync(string productId)
    {
        _productIds.State.Remove(productId);
        _productCache.Remove(productId);

        await _productIds.WriteStateAsync();
    }

    private async Task PopulateProductCacheAsync()
    {
        if (_productIds is not { State.Count: > 0 })
        {
            return;
        }

        await Parallel.ForEachAsync(
            _productIds.State,
            async (id, _) =>
            {
                var productGrain = GrainFactory.GetGrain<IProductGrain>(id);
                _productCache[id] = await productGrain.GetProductDetailsAsync();
            });
    }
}
