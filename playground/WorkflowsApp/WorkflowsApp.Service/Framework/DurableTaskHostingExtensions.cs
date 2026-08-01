using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.DurableTasks;
using Orleans.Runtime.DurableTasks;

namespace WorkflowsApp.Service;

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
