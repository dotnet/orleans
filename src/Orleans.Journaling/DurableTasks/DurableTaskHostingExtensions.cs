using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;

namespace Orleans.Journaling.DurableTasks;
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously

public static class DurableTaskHostingExtensions
{
    public static ISiloBuilder AddVolatileDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddTransient(static serviceProvider =>
            new VolatileDurableTaskGrainStorage(
                serviceProvider.GetRequiredService<DeepCopier<Dictionary<TaskId, DurableTaskState>>>(),
                serviceProvider.GetRequiredService<DeepCopier<DurableTaskState>>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetService<IDurableTaskMessageTransport>()));
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, VolatileDurableTaskGrainStorage>();
        return siloBuilder;
    }

    public static ISiloBuilder AddJournaledDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.TryAddScoped<DurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, DurableTaskGrainStorage>();
        return siloBuilder;
    }
}
