// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Grains;

[Reentrant]
internal sealed class InventoryGrain(
    [PersistentState(
            stateName: "Inventory",
            storageName: "shopping-cart")]
        IPersistentState<HashSet<string>> state) : Grain, IInventoryGrain
{
    private readonly Dictionary<string, ProductDetails> _productCache = [];
    private readonly StateManager _stateManager = new(state);

    public override async Task OnActivateAsync(CancellationToken cancellationToken) => await PopulateProductCacheAsync(cancellationToken);

    public ValueTask<int> GetProductCount() => new(_productCache.Count);

    public async IAsyncEnumerable<ProductDetails> GetAllProductsAsync()
    {
        // We await this to make the compiler happy.
        await Task.CompletedTask;

        var values = _productCache.Values.ToList();
        foreach (var value in values)
        {
            yield return value;
        }
    }

    public async ValueTask AddOrUpdateProductAsync(ProductDetails product)
    {
        ArgumentNullException.ThrowIfNull(product.Id);
        state.State.Add(product.Id);
        _productCache[product.Id] = product;

        await _stateManager.WriteStateAsync();
    }

    public async ValueTask RemoveProductAsync(string productId)
    {
        state.State.Remove(productId);
        _productCache.Remove(productId);

        await _stateManager.WriteStateAsync();
    }

    private async Task PopulateProductCacheAsync(CancellationToken cancellationToken)
    {
        if (state is not { State.Count: > 0 })
        {
            return;
        }

        await Parallel.ForEachAsync(
            state.State,
            new ParallelOptions
            {
                TaskScheduler = TaskScheduler.Current,
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 32,
            },
            async (id, ct) =>
            {
                var productGrain = GrainFactory.GetGrain<IProductGrain>(id);
                _productCache[id] = await productGrain.GetProductDetailsAsync().WaitAsync(ct);
            });
    }
}
