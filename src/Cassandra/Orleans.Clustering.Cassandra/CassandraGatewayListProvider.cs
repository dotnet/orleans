using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Clustering.Cassandra;

public class CassandraGatewayListProvider : IGatewayListProvider
{
    private readonly ClusterOptions _options;
    private readonly TimeSpan _maxStaleness;
    private readonly ISession _session;
    private OrleansQueries? _queries;
    private DateTime _cacheUntil;
    private List<Uri>? _cachedResult;
    private readonly string _identifier;


    TimeSpan IGatewayListProvider.MaxStaleness => _maxStaleness;

    bool IGatewayListProvider.IsUpdatable => true;


    public CassandraGatewayListProvider(IOptions<ClusterOptions> options, IOptions<GatewayOptions> gatewayOptions, ISession session)
    {
        _options = options.Value;
        _identifier = $"{_options.ServiceId}-{_options.ClusterId}";

        _maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        _session = session;
    }

    async Task IGatewayListProvider.InitializeGatewayListProvider()
    {
        _queries = await OrleansQueries.CreateInstance(_session);

        await _session.ExecuteAsync(_queries.EnsureTableExists());
        await _session.ExecuteAsync(_queries.EnsureIndexExists());
    }

    async Task<IList<Uri>> IGatewayListProvider.GetGateways()
    {
        if (_cachedResult is not null && _cacheUntil > DateTime.UtcNow)
        {
            return _cachedResult.ToList();
        }

        var rows = await _session.ExecuteAsync(await _queries!.GatewaysQuery(_identifier, (int)SiloStatus.Active));
        var result = new List<Uri>();

        foreach (var row in rows)
            result.Add(SiloAddress.New(new IPEndPoint(IPAddress.Parse((string)row["address"]), (int)row["proxy_port"]), (int)row["generation"]).ToGatewayUri());

        _cacheUntil = DateTime.UtcNow + _maxStaleness;
        _cachedResult = result;
        return result.ToList();
    }
}