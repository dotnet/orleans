using System.Net;
using Cassandra;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Tester.Cassandra.Journaling;

public sealed class CassandraJournalContainer : IAsyncLifetime
{
    private readonly object _lock = new();
    private Task<(IContainer Container, Cluster Cluster, ISession Session)>? _backend;

    public void EnsurePreconditionsMet()
        => Assert.SkipWhen(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CASSANDRAVERSION")), "CASSANDRAVERSION is not configured.");

    public async Task<ISession> GetSessionAsync(CancellationToken cancellationToken)
    {
        EnsurePreconditionsMet();
        Task<(IContainer Container, Cluster Cluster, ISession Session)> task;
        lock (_lock)
        {
            if (_backend is { IsCanceled: true } or { IsFaulted: true })
            {
                _backend = null;
            }

            task = _backend ??= StartAsync(cancellationToken);
        }

        return (await task.WaitAsync(cancellationToken)).Session;
    }

    public async ValueTask DisposeAsync()
    {
        Task<(IContainer Container, Cluster Cluster, ISession Session)>? backend;
        lock (_lock)
        {
            backend = _backend;
            _backend = null;
        }

        if (backend is { IsCompletedSuccessfully: true })
        {
            var result = await backend;
            result.Cluster.Dispose();
            await result.Container.DisposeAsync();
        }
    }

    private static async Task<(IContainer Container, Cluster Cluster, ISession Session)> StartAsync(CancellationToken cancellationToken)
    {
        var version = Environment.GetEnvironmentVariable("CASSANDRAVERSION");
        var image = $"cassandra:{version}";
        IContainer? container = null;
        try
        {
            const ushort port = 9042;
            container = new ContainerBuilder(image)
                .WithPortBinding(port, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(port))
                .Build();
            await container.StartAsync(cancellationToken);
            var exposedPort = container.GetMappedPublicPort(port);
            var cluster = Cluster.Builder()
                .WithDefaultKeyspace("orleans")
                .AddContactPoints(new IPEndPoint(IPAddress.Loopback, exposedPort))
                .Build();
            try
            {
                var session = cluster.ConnectAndCreateDefaultKeyspaceIfNotExists(
                    ReplicationStrategies.CreateSimpleStrategyReplicationProperty(1));
                return (container, cluster, session);
            }
            catch
            {
                cluster.Dispose();
                throw;
            }
        }
        catch
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }

            throw;
        }
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
}
