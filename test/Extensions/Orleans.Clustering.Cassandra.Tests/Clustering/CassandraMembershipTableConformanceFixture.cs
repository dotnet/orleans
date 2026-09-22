using System.Collections.Concurrent;
using System.Net;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.Cassandra;
using Orleans.Clustering.TestKit;

namespace Tester.Cassandra.Clustering;

public sealed partial class CassandraClusteringTableTests
{
    private const int ConformancePageSize = 32;
    private readonly ConcurrentBag<(IMembershipTable Table, string ClusterId, ServiceProvider Services)> _legacyMembershipHandles = [];

    protected override int ConformanceConcurrencyRowCount => ConformancePageSize + 1;

    protected override MembershipTableTestFixture CreateConformanceFixture()
    {
        var scope = new ConformanceScope(_cassandraContainer);
        return new("Cassandra", scope.CreateHandleAsync, scope.IsDeletedAsync);
    }

    private sealed class ConformanceScope(CassandraContainer container)
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (string PartitionKey, ISession Session)> _scopes = [];
        private Task<(Cluster Cluster, ISession Session, ISession ProbeSession)>? _session;
        private int _handleCount;
        private bool _disposed;

        public async ValueTask<MembershipTableTestHandle> CreateHandleAsync(
            string serviceId,
            string clusterId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<(Cluster Cluster, ISession Session, ISession ProbeSession)> creation;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Pending factories reserve ownership too, including a first connection which completes after timeout.
                _handleCount++;
                creation = _session ??= CreateSessionAsync(cancellationToken);
            }

            ServiceProvider? services = null;
            try
            {
                var (_, session, probeSession) = await creation;
                lock (_lock)
                {
                    _scopes[clusterId] = ($"{serviceId}-{clusterId}", probeSession);
                }

                services = CreateMembershipServices(serviceId, clusterId, () => Task.FromResult(session), cassandraTtl: false);
                var table = services.GetRequiredService<CassandraClusteringTable>();
                return new MembershipTableTestHandle(table, () => DisposeHandleAsync(services));
            }
            catch (Exception failure)
            {
                try
                {
                    await DisposeHandleAsync(services);
                }
                catch (Exception cleanup)
                {
                    failure.Data["ConformanceHandleDisposalFailure"] = cleanup;
                }

                throw;
            }
        }

        private async Task<(Cluster Cluster, ISession Session, ISession ProbeSession)> CreateSessionAsync(CancellationToken cancellationToken)
        {
            var backend = await container.RunImage(cancellationToken);
            var cluster = Cluster.Builder()
                .WithDefaultKeyspace("orleans")
                .AddContactPoints(new IPEndPoint(IPAddress.Loopback, backend.exposedPort))
                .WithQueryOptions(new QueryOptions().SetPageSize(ConformancePageSize))
                .Build();
            try
            {
                var session = await cluster.ConnectAsync("orleans");
                cancellationToken.ThrowIfCancellationRequested();
                return (cluster, session, backend.session);
            }
            catch
            {
                cluster.Dispose();
                throw;
            }
        }

        public async ValueTask<bool> IsDeletedAsync(string clusterId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string PartitionKey, ISession Session) scope;
            lock (_lock)
            {
                scope = _scopes[clusterId];
            }

            var statement = new SimpleStatement("SELECT * FROM membership WHERE partition_key = ? LIMIT 1", scope.PartitionKey)
                .SetConsistencyLevel(ConsistencyLevel.Serial)
                .SetPageSize(1);
            var rows = await scope.Session.ExecuteAsync(statement);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A static-version-only row still means the original partition has not been deleted.
                if (rows.GetAvailableWithoutFetching() > 0)
                {
                    return false;
                }

                if (rows.IsFullyFetched)
                {
                    return true;
                }

                await rows.FetchMoreResultsAsync();
            }
        }

        private async ValueTask DisposeHandleAsync(ServiceProvider? services)
        {
            try
            {
                if (services is not null)
                {
                    await services.DisposeAsync();
                }
            }
            finally
            {
                Cluster? cluster = null;
                lock (_lock)
                {
                    if (--_handleCount == 0)
                    {
                        _disposed = true;
                        if (_session is { IsCompletedSuccessfully: true })
                        {
                            cluster = _session.Result.Cluster;
                        }
                    }
                }

                cluster?.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var (table, clusterId, services) in _legacyMembershipHandles)
        {
            try
            {
                await table.DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await services.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Cassandra membership fixture cleanup failed.", failures);
        }
    }
}
