// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

﻿using System.Globalization;

namespace Orleans.ShoppingCart.Silo.StartupTasks;

public sealed class ProductStoreSeeder(ILogger<ProductStoreSeeder> logger, IGrainFactory grainFactory) : IHostedLifecycleService
{
    private const int TargetProductCount = 50;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var products = ProductDetailsExtensions.ProductDetailsFaker
            .Generate(TargetProductCount)
            .Select((product, index) => product with
            {
                Id = index.ToString(CultureInfo.InvariantCulture),
            })
            .ToList();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var initializer = grainFactory.GetGrain<ICatalogInitializerGrain>("default");
                    await initializer.EnsureSeededAsync(products).WaitAsync(cancellationToken);

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

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
