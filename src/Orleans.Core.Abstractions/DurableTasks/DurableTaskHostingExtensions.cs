using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;

namespace Orleans.DurableTasks;

internal static class DurableTaskHostingExtensions
{
    public static IServiceCollection AddDurableTaskSerialization(this IServiceCollection services) => services.AddSerializer(serializerConfig =>
    {
        serializerConfig.Configure(typeOptions =>
        {
            typeOptions.WellKnownTypeAliases[nameof(DurableTask)] = typeof(DurableTask);
            typeOptions.WellKnownTypeAliases[nameof(DurableTask) + "`1"] = typeof(DurableTask<>);
            typeOptions.WellKnownTypeAliases[nameof(TaskId)] = typeof(TaskId);
        });
    });
}
