using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.Journaling;

// <volatile_journal_thresholds>
internal static class VolatileJournalConfiguration
{
    internal static ISiloBuilder Configure(ISiloBuilder silo) =>
        silo.AddVolatileJournalStorage(options =>
        {
            options.MaxAppendsBeforeSnapshot = 100;
            options.MaxBytesBeforeSnapshot = 1024 * 1024;
        });
}
// </volatile_journal_thresholds>

// <arc_buffer_pool_budget>
internal static class JournalBufferConfiguration
{
    internal static void ConfigureProcess() =>
        ArcBufferWriter.MaxRetainedPoolBytes = 8 * 1024 * 1024;
}
// </arc_buffer_pool_budget>

// <journal_operation_hooks>
internal static class JournalHookRegistration
{
    internal static void Register(IJournaledStateManager owner, IJournaledStateHook featureHook)
    {
        var hooks = owner.Hooks;
        if (!hooks.Contains(featureHook))
        {
            hooks.Add(featureHook);
        }
    }

    internal static IJournaledStateHook Create(
        Func<JournaledStateOperation, CancellationToken, ValueTask> establishPrerequisites,
        Func<JournaledStateOperation, CancellationToken, ValueTask> completeCommittedWork) =>
        new JournaledStateHook
        {
            BeforeOperationAsync = establishPrerequisites,
            AfterOperationAsync = completeCommittedWork
        };
}
// </journal_operation_hooks>


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
