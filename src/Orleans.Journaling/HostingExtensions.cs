using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Journaling;
public static class HostingExtensions
{
    public static ISiloBuilder AddStateMachineStorage(this ISiloBuilder builder)
    {
        builder.Services.TryAddScoped<IStateMachineStorage>(sp => sp.GetRequiredService<IStateMachineStorageProvider>().Create(sp.GetRequiredService<IGrainContext>()));
        builder.Services.TryAddScoped<IStateMachineManager, StateMachineManager>();
        builder.Services.TryAddKeyedScoped(typeof(IDurableDictionary<,>), KeyedService.AnyKey, typeof(DurableDictionary<,>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableList<>), KeyedService.AnyKey, typeof(DurableList<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableQueue<>), KeyedService.AnyKey, typeof(DurableQueue<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableSet<>), KeyedService.AnyKey, typeof(DurableSet<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        builder.Services.TryAddKeyedScoped(typeof(IPersistentState<>), KeyedService.AnyKey, typeof(DurableState<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableTaskCompletionSource<>), KeyedService.AnyKey, typeof(DurableTaskCompletionSource<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableNothing), KeyedService.AnyKey, typeof(DurableNothing));
        return builder;
    }
}
