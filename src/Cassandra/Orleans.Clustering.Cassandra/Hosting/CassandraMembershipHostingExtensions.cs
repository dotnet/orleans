using System;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;

namespace Orleans.Clustering.Cassandra.Hosting;

/// <summary>
/// Extension methods for configuring Cassandra as a clustering provider.
/// </summary>
public static class CassandraMembershipHostingExtensions
{
    /// <summary>
    /// Configures Orleans clustering using Cassandra.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <returns>The client builder.</returns>
    public static IClientBuilder UseCassandraClustering(this IClientBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IGatewayListProvider, CassandraGatewayListProvider>();
        });

    /// <summary>
    /// Configures Orleans clustering using Cassandra.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="sessionProvider">A delegate used to create an <see cref="ISession"/>.</param>
    /// <returns>The client builder.</returns>
    public static IClientBuilder UseCassandraClustering(this IClientBuilder builder, Func<IServiceProvider, Task<ISession>> sessionProvider) =>
        builder.ConfigureServices(services =>
        {
            services.Configure<CassandraClusteringOptions>(o => o.ConfigureClient(sessionProvider));
            services.AddSingleton<IGatewayListProvider, CassandraGatewayListProvider>();
        });

    /// <summary>
    /// Configures Orleans clustering using Cassandra.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configureOptions">A delegate used to configure the Cassandra client.</param>
    /// <returns>The client builder.</returns>
    public static IClientBuilder UseCassandraClustering(this IClientBuilder builder, Action<CassandraClusteringOptions> configureOptions) =>
        builder.ConfigureServices(services =>
        {
            services.Configure<CassandraClusteringOptions>(configureOptions);
            services.AddSingleton<IGatewayListProvider, CassandraGatewayListProvider>();
        });

    /// <summary>
    /// Configures Orleans clustering using Cassandra.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="connectionString">The Cassandra connection string.</param>
    /// <param name="keyspace">The Cassandra keyspace, which defaults to <c>orleans</c>.</param>
    /// <returns>The client builder.</returns>
    public static IClientBuilder UseCassandraClustering(this IClientBuilder builder, string connectionString, string keyspace = "orleans") =>
        builder.ConfigureServices(services =>
        {
            services.Configure<CassandraClusteringOptions>(o => o.ConfigureClient(connectionString, keyspace));
            services.AddSingleton<IGatewayListProvider, CassandraGatewayListProvider>();
        });

    /// <summary>
    /// Configures Orleans clustering using Cassandra.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    /// <remarks>Pulls <see cref="IOptions{TOptions}"/> of type <see cref="ClusterOptions"/> and <see cref="ISession"/> from the DI container</remarks>
    public static ISiloBuilder UseCassandraClustering(this ISiloBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IMembershipTable, CassandraClusteringTable>();
        });

    /// <summary>
    /// Configures Orleans clustering using Cassandra.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="sessionProvider">A delegate used to create an <see cref="ISession"/>.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseCassandraClustering(this ISiloBuilder builder, Func<IServiceProvider, Task<ISession>> sessionProvider) =>
        builder.ConfigureServices(services =>
        {
            services.Configure<CassandraClusteringOptions>(o => o.ConfigureClient(sessionProvider));
            services.AddSingleton<IMembershipTable, CassandraClusteringTable>();
        });

    /// <summary>
    /// Configures Orleans clustering using Cassandra.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">A delegate used to configure the Cassandra client.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseCassandraClustering(this ISiloBuilder builder, Action<CassandraClusteringOptions> configureOptions) =>
        builder.ConfigureServices(services =>
        {
            services.Configure<CassandraClusteringOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, CassandraClusteringTable>();
        });

    /// <summary>
    /// Configures Orleans clustering using Cassandra.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="connectionString">The Cassandra connection string.</param>
    /// <param name="keyspace">The Cassandra keyspace, which defaults to <c>orleans</c>.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseCassandraClustering(this ISiloBuilder builder, string connectionString, string keyspace = "orleans") =>
        builder.ConfigureServices(services =>
        {
            services.Configure<CassandraClusteringOptions>(o => o.ConfigureClient(connectionString, keyspace));
            services.AddSingleton<IMembershipTable, CassandraClusteringTable>();
        });
}