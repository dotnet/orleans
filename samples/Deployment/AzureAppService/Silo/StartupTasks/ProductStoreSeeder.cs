// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.StartupTasks;

public sealed class ProductStoreSeeder(ILogger<ProductStoreSeeder> logger, IGrainFactory grainFactory) : IHostedLifecycleService
{
    private const int TargetProductCount = 50;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var faker = ProductDetailsExtensions.ProductDetailsFaker;
                    var productCount = await GetProductCount(cancellationToken);

                    if (productCount >= TargetProductCount)
                    {
                        return;
                    }

                    foreach (var product in faker.GenerateLazy(TargetProductCount - productCount))
                    {
                        var productGrain = grainFactory.GetGrain<IProductGrain>(product.Id);
                        await productGrain.CreateOrUpdateProductAsync(product).WaitAsync(cancellationToken);
                    }

                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Error while seeding product catalog.");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async ValueTask<int> GetProductCount(CancellationToken cancellationToken)
    {
        var sum = 0;
        await Parallel.ForEachAsync(
            Enum.GetNames<ProductCategory>(),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 32,
            },
            async (category, ct) =>
            {
                var categoryGrain = grainFactory.GetGrain<IInventoryGrain>(category);
                var categoryCount = await categoryGrain.GetProductCount();
                Interlocked.Add(ref sum, categoryCount);
            });

        return sum;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
