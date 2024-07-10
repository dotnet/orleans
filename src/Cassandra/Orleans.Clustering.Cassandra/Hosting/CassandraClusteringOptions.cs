using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Cassandra;

namespace Orleans.Clustering.Cassandra.Hosting;

/// <summary>
/// Options for configuring Cassandra clustering.
/// </summary>
public class CassandraClusteringOptions
{
    /// <summary>
    /// Configures the Cassandra client.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="keyspace">The keyspace.</param>
    public void ConfigureClient(string connectionString, string keyspace = "orleans")
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(keyspace);
        CreateSessionAsync = async sp =>
        {
            var c = Cluster.Builder().WithConnectionString(connectionString)
                .Build();

            var session = await c.ConnectAsync(keyspace).ConfigureAwait(false);
            return session;
        };
    }

    /// <summary>
    /// Configures the Cassandra client.
    /// </summary>
    /// <param name="configurationDelegate">The connection string.</param>
    public void ConfigureClient(Func<IServiceProvider, Task<ISession>> configurationDelegate)
    {
        ArgumentNullException.ThrowIfNull(configurationDelegate);
        CreateSessionAsync = configurationDelegate;
    }

    [NotNull]
    internal Func<IServiceProvider, Task<ISession>> CreateSessionAsync { get; private set; } = default!;
}