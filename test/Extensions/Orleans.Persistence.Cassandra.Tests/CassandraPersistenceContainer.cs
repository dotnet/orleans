using System.Net;
using Cassandra;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Orleans.Persistence.Cassandra.Tests;

public sealed class CassandraPersistenceContainer : IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    private readonly string? _image = GetImage();
    private readonly object _lock = new();
    private IContainer? _container;
    private Cluster? _cluster;
    private ISession? _session;
    private Task? _startTask;
    private CancellationTokenSource? _startCancellation;
    private Task? _disposeTask;
    private bool _disposed;

    public ISession Session
    {
        get
        {
            lock (_lock)
            {
                return _session ?? throw new InvalidOperationException("The Cassandra container has not started.");
            }
        }
    }

    public void EnsurePreconditionsMet() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(_image), "CASSANDRAVERSION is not configured.");

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        EnsurePreconditionsMet();

        Task startTask;
        lock (_lock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CassandraPersistenceContainer));
            }

            if (_startTask is { IsCanceled: true } or { IsFaulted: true })
            {
                _startTask = null;
                _startCancellation?.Dispose();
                _startCancellation = null;
            }

            if (_startTask is null)
            {
                _startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _startTask = StartCoreAsync(_startCancellation.Token);
            }

            startTask = _startTask;
        }

        try
        {
            await startTask.WaitAsync(cancellationToken);
        }
        catch
        {
            lock (_lock)
            {
                if (ReferenceEquals(_startTask, startTask)
                    && startTask.IsCompleted
                    && !startTask.IsCompletedSuccessfully)
                {
                    _startTask = null;
                    _startCancellation?.Dispose();
                    _startCancellation = null;
                }
            }

            throw;
        }
    }

    public ISession OpenSession()
    {
        EnsurePreconditionsMet();
        lock (_lock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CassandraPersistenceContainer));
            }

            return _cluster?.Connect("orleans")
                ?? throw new InvalidOperationException("The Cassandra container has not started.");
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        const ushort containerPort = 9042;
        var container = new ContainerBuilder(_image!)
            .WithPortBinding(containerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(containerPort))
            .Build();
        Cluster? cluster = null;
        ISession? session = null;

        try
        {
            await container.StartAsync(cancellationToken);
            var exposedPort = container.GetMappedPublicPort(containerPort);
            cluster = Cluster.Builder()
                .WithDefaultKeyspace("orleans")
                .AddContactPoints(new IPEndPoint(IPAddress.Loopback, exposedPort))
                .Build();
            session = cluster.ConnectAndCreateDefaultKeyspaceIfNotExists(
                ReplicationStrategies.CreateSimpleStrategyReplicationProperty(1));
            lock (_lock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(CassandraPersistenceContainer));
                }

                _container = container;
                _cluster = cluster;
                _session = session;
            }
        }
        catch
        {
            session?.Dispose();
            cluster?.Dispose();
            await CleanupAsync(container, CancellationToken.None, suppressExceptions: true);
            throw;
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(CancellationToken.None);

    public async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        Task disposeTask;
        lock (_lock)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        await disposeTask.WaitAsync(cancellationToken);
    }

    private async Task DisposeCoreAsync()
    {
        Task? startTask;
        CancellationTokenSource? startCancellation;
        lock (_lock)
        {
            _disposed = true;
            startTask = _startTask;
            startCancellation = _startCancellation;
            startCancellation?.Cancel();
        }

        if (startTask is not null)
        {
            try
            {
                await startTask.ConfigureAwait(false);
            }
            catch
            {
                // Startup owns cleanup for resources which were not published.
            }
        }

        IContainer? container;
        Cluster? cluster;
        ISession? session;
        lock (_lock)
        {
            container = _container;
            cluster = _cluster;
            session = _session;
            _container = null;
            _cluster = null;
            _session = null;
            _startTask = null;
            _startCancellation = null;
        }

        startCancellation?.Dispose();
        session?.Dispose();
        cluster?.Dispose();
        if (container is not null)
        {
            await CleanupAsync(container, CancellationToken.None, suppressExceptions: false);
        }
    }

    private static async Task CleanupAsync(
        IContainer container,
        CancellationToken cancellationToken,
        bool suppressExceptions)
    {
        try
        {
            using var cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cleanupCancellation.CancelAfter(CleanupTimeout);
            await container.DisposeAsync().AsTask().WaitAsync(cleanupCancellation.Token);
        }
        catch when (suppressExceptions)
        {
            // Preserve the startup failure.
        }
    }

    private static string? GetImage()
    {
        var version = Environment.GetEnvironmentVariable("CASSANDRAVERSION");
        return string.IsNullOrWhiteSpace(version) ? null : $"cassandra:{version}";
    }
}
