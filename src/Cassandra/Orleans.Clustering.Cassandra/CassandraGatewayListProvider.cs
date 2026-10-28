using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Clustering.Cassandra;

internal sealed class CassandraGatewayListProvider : IGatewayListProvider, IDisposable
{
    private const string NotInitializedMessage = $"This instance has not been initialized. Ensure that {nameof(IGatewayListProvider.InitializeGatewayListProvider)} is called to initialize this instance before use.";
    private readonly TimeSpan _maxStaleness;
    private readonly string _identifier;
    private readonly CassandraClusteringOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private ISession? _session;
    private OrleansQueries? _queries;
    private DateTime _cacheUntil;
    private List<Uri>? _cachedResult;

    TimeSpan IGatewayListProvider.MaxStaleness => _maxStaleness;

    bool IGatewayListProvider.IsUpdatable => true;

    public CassandraGatewayListProvider(
        IOptions<ClusterOptions> clusterOptions,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<CassandraClusteringOptions> options,
        IOptions<ClusterMembershipOptions> clusterMembershipOptions,
        IServiceProvider serviceProvider)
    {
        _identifier = $"{clusterOptions.Value.ServiceId}-{clusterOptions.Value.ClusterId}";
        _options = options.Value;
        _serviceProvider = serviceProvider;

        _maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
    }

    private OrleansQueries Queries => _queries ?? throw new InvalidOperationException(NotInitializedMessage);

    Task IGatewayListProvider.InitializeGatewayListProvider() => InitializeGatewayListProviderAsync(CancellationToken.None);

    public async Task InitializeGatewayListProviderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var creation = _options.CreateSessionAsync(_serviceProvider);
        ISession session;
        try
        {
            session = await OrleansQueries.AwaitAsync(creation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_options.OwnsSession)
            {
                DisposeAbandonedSessionAsync(creation).Ignore();
            }

            throw;
        }

        if (session is null)
        {
            throw new InvalidOperationException($"Session created from configuration '{nameof(CassandraClusteringOptions)}' is null.");
        }

        try
        {
            var queries = await OrleansQueries.CreateInstance(session);
            await queries.EnsureTableExistsAsync(_options.InitializeRetryMaxDelay, cancellationToken);
            _session = session;
            _queries = queries;
        }
        catch
        {
            if (_options.OwnsSession)
            {
                session.Cluster.Dispose();
            }

            throw;
        }
    }

    Task<IList<Uri>> IGatewayListProvider.GetGateways() => GetGatewaysAsync(CancellationToken.None);

    public async Task<IList<Uri>> GetGatewaysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cachedResult is not null && _cacheUntil > DateTime.UtcNow)
        {
            return [.. _cachedResult];
        }

        var rows = await Queries.ExecuteAsync(
            await Queries.GatewaysQuery(_identifier, (int)SiloStatus.Active, cancellationToken),
            cancellationToken);
        var result = new List<Uri>();

        await foreach (var row in OrleansQueries.ReadRowsAsync(rows, cancellationToken))
        {
            result.Add(SiloAddress.New(new IPEndPoint(IPAddress.Parse((string)row["address"]), (int)row["proxy_port"]), (int)row["generation"]).ToGatewayUri());
        }

        _cacheUntil = DateTime.UtcNow + _maxStaleness;
        _cachedResult = result;
        return [.. result];
    }

    private static async Task DisposeAbandonedSessionAsync(Task<ISession> creation)
    {
        var session = await creation.ConfigureAwait(false);
        session?.Cluster.Dispose();
    }

    public void Dispose()
    {
        if (_options.OwnsSession)
        {
            _session?.Cluster.Dispose();
        }

        _session = null;
        _queries = null;
    }
}
