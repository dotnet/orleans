using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.Journaling;

// <composed_shopping_cart>
public interface IShoppingCartGrain : IGrainWithStringKey
{
    ValueTask AddItem(string itemId, int quantity, CancellationToken cancellationToken);
    ValueTask<Dictionary<string, int>> GetItems(CancellationToken cancellationToken);
}

public sealed class ShoppingCartGrain(IDurableStateManager stateManager)
    : Grain, IShoppingCartGrain
{
    private readonly IDurableDictionary<string, int> _cart =
        stateManager.GetOrAddDictionary<string, int>("cart");

    public async ValueTask AddItem(string itemId, int quantity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cart[itemId] = quantity;
        await stateManager.WriteStateAsync(cancellationToken);
    }

    public ValueTask<Dictionary<string, int>> GetItems(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(_cart.ToDictionary());
    }
}
// </composed_shopping_cart>

// <journaled_feature>
public sealed class CartActivationCounter(
    IDurableStateManager stateManager,
    [FromKeyedServices("activation-count")] IDurableValue<int> count)
    : ILifecycleParticipant<IGrainLifecycle>
{
    public void Participate(IGrainLifecycle lifecycle)
    {
        lifecycle.Subscribe<CartActivationCounter>(
            GrainLifecycleStage.Activate - 1,
            async cancellationToken =>
            {
                count.Value++;
                await stateManager.WriteStateAsync(cancellationToken);
            });
    }
}

public sealed class CartFeatureConfigurator(GrainClassMap grainClasses)
    : IConfigureGrainTypeComponents
{
    public void Configure(
        GrainType grainType,
        GrainProperties properties,
        GrainTypeSharedContext shared)
    {
        if (grainClasses.TryGetGrainClass(grainType, out var grainClass)
            && typeof(IShoppingCartGrain).IsAssignableFrom(grainClass))
        {
            shared.AddActivationSetup(static context =>
            {
                var feature = context.ActivationServices
                    .GetRequiredService<CartActivationCounter>();
                feature.Participate(context.ObservableLifecycle);
            });
        }
    }
}
// </journaled_feature>

internal static class FeatureConfiguration
{
    internal static void Configure(ISiloBuilder siloBuilder)
    {
        // <journaled_feature_registration>
        siloBuilder.ConfigureServices(services =>
        {
            services.AddScoped<CartActivationCounter>();
            services.AddSingleton<IConfigureGrainTypeComponents, CartFeatureConfigurator>();
        });
        // </journaled_feature_registration>
    }
}
