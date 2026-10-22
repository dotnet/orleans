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
    /// Gets or sets whether Cassandra expires Dead membership rows after
    /// <see cref="ClusterMembershipOptions.DefunctSiloExpiration"/>.
    /// </summary>
    /// <remarks>
    /// Live membership rows and the table version remain persistent. Full-row Dead writes assign the retention
    /// period to all row fields together. Owner heartbeats persist only the heartbeat column. A late heartbeat
    /// after a Dead write can leave a heartbeat-only fragment when the remaining fields expire; membership reads
    /// retire that row when its start time expires. Explicit cleanup removes the whole row while preserving the
    /// table version. This applies to writes in both new and existing tables.
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
