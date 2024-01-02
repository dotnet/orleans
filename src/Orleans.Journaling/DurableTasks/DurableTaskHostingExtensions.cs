using Orleans.Configuration.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableTasks;
using Orleans.Runtime.DurableTasks;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.DurableTasks;
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously

public static class DurableTaskHostingExtensions
{
    public static ISiloBuilder AddVolatileDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddTransient<VolatileDurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, VolatileDurableTaskGrainStorage>();
        return siloBuilder;
    }

    public static ISiloBuilder AddJournaledDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.TryAddSingleton<DurableTaskGrainStorageShared>();
        siloBuilder.Services.TryAddScoped<DurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, DurableTaskGrainStorage>();
        return siloBuilder;
    }
}
