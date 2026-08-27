using System.Net;
using Cassandra;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Tester.Cassandra.Clustering;

public class CassandraContainer
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    private readonly object _lock = new();
    private Task<(IContainer container, ushort exposedPort, Cluster cluster, ISession session)>? _runImage;

    public async Task<(IContainer container, ushort exposedPort, Cluster cluster, ISession session)> RunImage(
        CancellationToken cancellationToken)
    {
        Task<(IContainer container, ushort exposedPort, Cluster cluster, ISession session)> task;
        lock (_lock)
        {
            if (_runImage is { IsCanceled: true } or { IsFaulted: true })
            {
                _runImage = null;
            }

            task = _runImage ??= RunImageCore(cancellationToken);
        }

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch
        {
            lock (_lock)
            {
                if (ReferenceEquals(_runImage, task)
                    && task.IsCompleted
                    && !task.IsCompletedSuccessfully)
                {
                    _runImage = null;
                }
            }

            throw;
        }
    }

    private static async Task<(IContainer container, ushort exposedPort, Cluster cluster, ISession session)> RunImageCore(
        CancellationToken cancellationToken)
    {
        var containerPort = 9042;
        IContainer? container = null;

        try
        {
            container = new ContainerBuilder("cassandra:" + Environment.GetEnvironmentVariable("CASSANDRAVERSION"))
                .WithPortBinding(containerPort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(containerPort))
                .Build();

            await container.StartAsync(cancellationToken);

            var exposedPort = container.GetMappedPublicPort(containerPort);

            var cluster = Cluster.Builder()
                .WithDefaultKeyspace("orleans")
                .AddContactPoints(new IPEndPoint(IPAddress.Loopback, exposedPort))
                .Build();

            // Connect to the nodes using a keyspace
            var session =
                cluster.ConnectAndCreateDefaultKeyspaceIfNotExists(ReplicationStrategies
                    .CreateSimpleStrategyReplicationProperty(1));

            return (container, exposedPort, cluster, session);
        }
        catch
        {
            if (container is not null)
            {
                try
                {
                    using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
                    await container.DisposeAsync().AsTask().WaitAsync(cleanupCancellation.Token);
                }
                catch
                {
                    // Preserve the startup failure.
                }
            }

            throw;
        }
    }

    public string Name { get; set; } = string.Empty;
}
