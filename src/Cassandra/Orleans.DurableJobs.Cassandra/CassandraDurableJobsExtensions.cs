using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring Cassandra durable jobs.
/// </summary>
public static class CassandraDurableJobsExtensions
{
    /// <summary>
    /// Adds durable jobs storage backed by Cassandra.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">The delegate used to configure Cassandra journal storage.</param>
    /// <returns>The silo builder, for chaining.</returns>
    public static ISiloBuilder UseCassandraDurableJobs(
        this ISiloBuilder builder,
        Action<CassandraJournalStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.AddCassandraJournalStorage(configure).UseJournaledDurableJobs();
    }

    /// <summary>
    /// Adds durable jobs storage backed by Cassandra.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The delegate used to configure Cassandra journal storage.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection UseCassandraDurableJobs(
        this IServiceCollection services,
        Action<CassandraJournalStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        new ServiceCollectionSiloBuilder(services).UseCassandraDurableJobs(configure);
        return services;
    }

    private sealed class ServiceCollectionSiloBuilder(IServiceCollection services) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
