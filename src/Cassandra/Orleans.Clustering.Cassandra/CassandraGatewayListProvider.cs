using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Clustering.Cassandra;

internal sealed class CassandraGatewayListProvider : IGatewayListProvider
{
    private const string NotInitializedMessage = $"This instance has not been initialized. Ensure that {nameof(IGatewayListProvider.InitializeGatewayListProvider)} is called to initialize this instance before use.";
    private readonly ClusterOptions _clusterOptions;
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
        IServiceProvider serviceProvider)
    {
        _clusterOptions = clusterOptions.Value;
        _identifier = $"{_clusterOptions.ServiceId}-{_clusterOptions.ClusterId}";
        _options = options.Value;
        _serviceProvider = serviceProvider;

        _maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
    }

    private ISession Session => _session ?? throw new InvalidOperationException(NotInitializedMessage);

    private OrleansQueries Queries => _queries ?? throw new InvalidOperationException(NotInitializedMessage);

    async Task IGatewayListProvider.InitializeGatewayListProvider()
    {
        _session = await _options.CreateSessionAsync(_serviceProvider);
        if (_session is null)
        {
            throw new InvalidOperationException($"Session created from configuration '{nameof(CassandraClusteringOptions)}' is null.");
        }

        _queries = await OrleansQueries.CreateInstance(_session);

        await _session.ExecuteAsync(_queries.EnsureTableExists());
        await _session.ExecuteAsync(_queries.EnsureIndexExists);
    }

    async Task<IList<Uri>> IGatewayListProvider.GetGateways()
    {
        if (_cachedResult is not null && _cacheUntil > DateTime.UtcNow)
        {
            return [.. _cachedResult];
        }

        var rows = await Session.ExecuteAsync(await Queries.GatewaysQuery(_identifier, (int)SiloStatus.Active));
        var result = new List<Uri>();

        foreach (var row in rows)
        {
            result.Add(SiloAddress.New(new IPEndPoint(IPAddress.Parse((string)row["address"]), (int)row["proxy_port"]), (int)row["generation"]).ToGatewayUri());
        }

        _cacheUntil = DateTime.UtcNow + _maxStaleness;
        _cachedResult = result;
        return [.. result];
    }
}