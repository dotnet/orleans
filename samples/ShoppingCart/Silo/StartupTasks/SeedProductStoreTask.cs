// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.StartupTasks;

public sealed class SeedProductStoreTask(IGrainFactory grainFactory) : IStartupTask
{
    async Task IStartupTask.Execute(CancellationToken cancellationToken)
    {
        var faker = new ProductDetails().GetBogusFaker();

        foreach (var product in faker.GenerateLazy(50))
        {
            var productGrain = grainFactory.GetGrain<IProductGrain>(product.Id);
            await productGrain.CreateOrUpdateProductAsync(product);
        }
    }
}
