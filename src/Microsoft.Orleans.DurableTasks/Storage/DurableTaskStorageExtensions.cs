using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Configuration.Internal;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring durable task state storage.
/// </summary>
public static class DurableTaskStorageExtensions
{
    /// <summary>
    /// Configures durable tasks to store state in memory for the lifetime of each grain activation.
    /// </summary>
    /// <param name="siloBuilder">The silo builder.</param>
    /// <returns>The provided silo builder.</returns>
    public static ISiloBuilder AddVolatileDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddScoped<VolatileDurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, VolatileDurableTaskGrainStorage>();
        return siloBuilder;
    }

    /// <summary>
    /// Configures durable tasks to store state in the grain's journaled state.
    /// </summary>
    /// <param name="siloBuilder">The silo builder.</param>
    /// <returns>The provided silo builder.</returns>
    public static ISiloBuilder AddJournaledDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.TryAddScoped<DurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, DurableTaskGrainStorage>();
        return siloBuilder;
    }
}
