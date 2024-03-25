using System;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Clustering.Cassandra.Hosting;

public static class CassandraMembershipHostingExtensions
{
    /// <summary>
    /// Configures Orleans clustering using Cassandra
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    /// <remarks>Pulls <see cref="IOptions{TOptions}"/> of type <see cref="ClusterOptions"/> and <see cref="ISession"/> from the DI container</remarks>
    public static ISiloBuilder UseCassandraClustering(this ISiloBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.AddOptions<ClusterOptions>().ValidateOnStart();
            services.AddSingleton<IMembershipTable, CassandraClusteringTable>();
        });

    /// <summary>
    /// Configures Orleans clustering using Cassandra
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="clusterOptions">Configuration for the <see cref="CassandraClusteringTable"/></param>
    /// <param name="sessionProvider">Resolving method for <see cref="ISession"/></param>
    /// <returns>A newly created <see cref="CassandraClusteringTable"/> created with the provided <see cref="ClusterOptions"/> and the resolved <see cref="ISession"/></returns>
    public static ISiloBuilder UseCassandraClustering(this ISiloBuilder builder, ClusterOptions clusterOptions, Func<IServiceProvider, ISession> sessionProvider) =>
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IMembershipTable>(provider =>
            {
                var session = sessionProvider(provider);
                return new CassandraClusteringTable(Options.Create(clusterOptions), session);
            });
        });


    /// <summary>
    /// Configures Orleans clustering using Cassandra
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="clusterOptions">Configuration for the <see cref="CassandraClusteringTable"/></param>
    /// <param name="cassandraOptions">Configuration used to create a new <see cref="ISession"/></param>
    /// <returns></returns>
    public static ISiloBuilder UseCassandraClustering(this ISiloBuilder builder, ClusterOptions clusterOptions, CassandraClusteringOptions cassandraOptions) =>
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IMembershipTable>(_ =>
            {
                var c = Cluster.Builder().WithConnectionString(cassandraOptions.ConnectionString)
                    .Build();

                var session = c.Connect(cassandraOptions.Keyspace);
                return new CassandraClusteringTable(Options.Create(clusterOptions), session);
            });
        });
}