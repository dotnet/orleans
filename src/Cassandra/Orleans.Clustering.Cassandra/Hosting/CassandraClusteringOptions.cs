using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Cassandra;
using Orleans.Configuration;

namespace Orleans.Clustering.Cassandra.Hosting;

/// <summary>
/// Options for configuring Cassandra clustering.
/// </summary>
public class CassandraClusteringOptions
{
    /// <summary>
    /// Optionally configure time-to-live behavior for the membership table row data in Cassandra itself, allowing
    /// defunct silo cleanup even if a cluster is no longer running.
    /// <para/>
    /// When this is <c>true</c>, <see cref="ClusterMembershipOptions.DefunctSiloCleanupPeriod"/> CAN be null to enable
    /// Cassandra-only defunct silo cleanup. Either way, the Cassandra TTL will still be configured from the
    /// configured <see cref="ClusterMembershipOptions.DefunctSiloExpiration"/> value.
    /// </summary>
    /// <remarks>
    /// Initial implementation of https://github.com/dotnet/orleans/issues/9164 in that it only affects silo entries
    /// that are updated with IAmAlive and will not attempt to update, for instance, the entire membership table. It
    /// also will not affect membership tables that have already been created, since it uses the Cassandra table-level
    /// <c>default_time_to_live</c>.
    /// </remarks>
    public bool UseCassandraTtl { get; set; }

    /// <summary>
    /// Specifies the maximum amount of time to wait after encountering
    /// contention during initialization before retrying.
    /// </summary>
    /// <remarks>This is generally only encountered with large numbers of silos connecting
    /// in a short time period and using multi-datacenter Cassandara clusters</remarks>
    public TimeSpan InitializeRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(20);

    internal int? GetCassandraTtlSeconds(ClusterMembershipOptions clusterMembershipOptions) =>
        UseCassandraTtl
            ? Convert.ToInt32(
                Math.Round(
                    clusterMembershipOptions.DefunctSiloExpiration.TotalSeconds,
                    MidpointRounding.AwayFromZero))
            : null;

    /// <summary>
    /// Configures the Cassandra client.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="keyspace">The keyspace.</param>
    /// <remarks>The membership provider owns the created cluster and disposes it if initialization is canceled or the provider is disposed.</remarks>
    public void ConfigureClient(string connectionString, string keyspace = "orleans")
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(keyspace);
        OwnsSession = true;
        CreateSessionAsync = async sp =>
        {
            var c = Cluster.Builder().WithConnectionString(connectionString)
                .Build();

            var connected = false;
            try
            {
                var session = await c.ConnectAsync(keyspace).ConfigureAwait(false);
                connected = true;
                return session;
            }
            finally
            {
                if (!connected)
                {
                    c.Dispose();
                }
            }
        };
    }

    /// <summary>
    /// Configures the Cassandra client.
    /// </summary>
    /// <param name="configurationDelegate">The connection string.</param>
    /// <remarks>Sessions returned by this delegate remain owned by the caller and are not disposed by the membership provider.</remarks>
    public void ConfigureClient(Func<IServiceProvider, Task<ISession>> configurationDelegate)
    {
        ArgumentNullException.ThrowIfNull(configurationDelegate);
        OwnsSession = false;
        CreateSessionAsync = configurationDelegate;
    }

    [NotNull]
    internal Func<IServiceProvider, Task<ISession>> CreateSessionAsync { get; private set; } = default!;

    internal bool OwnsSession { get; private set; }
}
