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
    private readonly Dictionary<string, (string PartitionKey, ISession Session)> _conformanceScopes = [];
    private Task<(Cluster Cluster, ISession Session, ISession ProbeSession)>? _conformanceSession;

    protected override int ConformanceConcurrencyRowCount => ConformancePageSize + 1;

    protected override MembershipTableTestFixture CreateConformanceFixture()
        => new("Cassandra", CreateConformanceHandleAsync, IsConformanceClusterDeletedAsync);

    private async ValueTask<MembershipTableTestHandle> CreateConformanceHandleAsync(
        string serviceId,
        string clusterId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, session, probeSession) = await (_conformanceSession ??= CreateConformanceSessionAsync(cancellationToken));
        _conformanceScopes[clusterId] = ($"{serviceId}-{clusterId}", probeSession);
        var services = CreateMembershipServices(serviceId, clusterId, () => Task.FromResult(session), cassandraTtl: false);
        var table = services.GetRequiredService<CassandraClusteringTable>();
        return new MembershipTableTestHandle(table, services.DisposeAsync);
    }

    private async Task<(Cluster Cluster, ISession Session, ISession ProbeSession)> CreateConformanceSessionAsync(CancellationToken cancellationToken)
    {
        var container = await _cassandraContainer.RunImage(cancellationToken);
        var cluster = Cluster.Builder()
            .WithDefaultKeyspace("orleans")
            .AddContactPoints(new IPEndPoint(IPAddress.Loopback, container.exposedPort))
            .WithQueryOptions(new QueryOptions().SetPageSize(ConformancePageSize))
            .Build();
        try
        {
            var session = await cluster.ConnectAsync("orleans");
            cancellationToken.ThrowIfCancellationRequested();
            return (cluster, session, container.session);
        }
        catch
        {
            cluster.Dispose();
            throw;
        }
    }

    private async ValueTask<bool> IsConformanceClusterDeletedAsync(string clusterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (partitionKey, session) = _conformanceScopes[clusterId];
        var statement = new SimpleStatement("SELECT * FROM membership WHERE partition_key = ?", partitionKey)
            .SetConsistencyLevel(ConsistencyLevel.Serial)
            .SetPageSize(ConformancePageSize);
        var rows = await session.ExecuteAsync(statement).WaitAsync(cancellationToken);
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

            await rows.FetchMoreResultsAsync().WaitAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var (table, clusterId, services) in _legacyMembershipHandles)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await table.DeleteMembershipTableEntriesAsync(clusterId, cleanup.Token).WaitAsync(cleanup.Token);
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

        if (_conformanceSession is { IsCompletedSuccessfully: true } session)
        {
            try
            {
                (await session).Cluster.Dispose();
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
