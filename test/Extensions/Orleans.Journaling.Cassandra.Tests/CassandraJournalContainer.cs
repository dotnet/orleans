using System.Net;
using Cassandra;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Tester.Cassandra.Journaling;

public sealed class CassandraJournalContainer : IAsyncLifetime
{
    private const string DefaultCassandraVersion = "5.0";
    private readonly object _lock = new();
    private readonly Func<CancellationToken, Task<(IContainer Container, Cluster Cluster, ISession Session)>> _startBackendAsync;
    private Task<(IContainer Container, Cluster Cluster, ISession Session)>? _backend;

    public CassandraJournalContainer() : this(StartAsync)
    {
    }

    internal CassandraJournalContainer(
        Func<CancellationToken, Task<(IContainer Container, Cluster Cluster, ISession Session)>> startBackendAsync)
    {
        _startBackendAsync = startBackendAsync ?? throw new ArgumentNullException(nameof(startBackendAsync));
    }

    public async Task<ISession> GetSessionAsync(CancellationToken cancellationToken)
    {
        Task<(IContainer Container, Cluster Cluster, ISession Session)> task;
        lock (_lock)
        {
            if (_backend is { IsCanceled: true } or { IsFaulted: true })
            {
                _backend = null;
            }

            task = _backend ??= _startBackendAsync(cancellationToken);
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

        if (backend is not null)
        {
            var result = await backend.ConfigureAwait(false);
            try
            {
                result.Cluster.Dispose();
            }
            finally
            {
                await result.Container.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<(IContainer Container, Cluster Cluster, ISession Session)> StartAsync(CancellationToken cancellationToken)
    {
        var image = GetImageName(Environment.GetEnvironmentVariable("CASSANDRAVERSION"));
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

    internal static string GetImageName(string? version)
        => $"cassandra:{(string.IsNullOrWhiteSpace(version) ? DefaultCassandraVersion : version)}";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
}
