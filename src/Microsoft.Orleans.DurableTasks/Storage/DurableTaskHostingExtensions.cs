using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Configuration.Internal;
using Orleans.Hosting;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring durable task storage.
/// </summary>
public static class DurableTaskStorageExtensions
{
    /// <summary>
    /// Configures volatile, in-memory durable task storage.
    /// </summary>
    /// <param name="siloBuilder">The silo builder.</param>
    /// <returns>The provided silo builder.</returns>
    public static ISiloBuilder AddVolatileDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddTransient<VolatileDurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, VolatileDurableTaskGrainStorage>();
        return siloBuilder;
    }

    /// <summary>
    /// Configures journal-backed durable task storage.
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
